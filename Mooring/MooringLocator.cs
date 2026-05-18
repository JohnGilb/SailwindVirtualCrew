using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum MooringSide
    {
        Port,
        Starboard
    }

    internal enum MooringBeamAxis
    {
        LocalX,
        LocalZ
    }

    internal sealed class MooringScan
    {
        internal CrewBoatContext Context { get; set; }
        internal BoatMooringRopes MooringRopes { get; set; }
        internal MooringSideMap SideMap { get; set; }
        internal List<MooringRopeInfo> Ropes { get; } = new List<MooringRopeInfo>();
        internal List<MooringDockInfo> Docks { get; } = new List<MooringDockInfo>();
    }

    internal sealed class MooringSideMap
    {
        internal MooringBeamAxis Axis { get; }
        internal bool PortPositive { get; }

        internal MooringSideMap(MooringBeamAxis axis, bool portPositive)
        {
            Axis = axis;
            PortPositive = portPositive;
        }

        internal string AxisName => Axis == MooringBeamAxis.LocalX ? "X" : "Z";

        internal float AxisValue(Vector3 local)
        {
            return Axis == MooringBeamAxis.LocalX ? local.x : local.z;
        }

        internal MooringSide Classify(Vector3 local)
        {
            float value = AxisValue(local);
            return (value > 0f) == PortPositive ? MooringSide.Port : MooringSide.Starboard;
        }
    }

    internal sealed class MooringRopeInfo
    {
        internal int Index { get; set; }
        internal PickupableBoatMooringRope Rope { get; set; }
        internal Vector3 AnchorWorld { get; set; }
        internal Vector3 AnchorLocal { get; set; }
        internal Vector3 CurrentLocal { get; set; }
        internal MooringSide Side { get; set; }
        internal bool IsMoored { get; set; }
    }

    internal sealed class MooringDockInfo
    {
        internal int Index { get; set; }
        internal GPButtonDockMooring Mooring { get; set; }
        internal Vector3 LocalPosition { get; set; }
        internal float DistanceToBoat { get; set; }
        internal bool IsOccupied { get; set; }
        internal MooringSide Side { get; set; }
    }

    internal sealed class MooringPair
    {
        internal MooringRopeInfo Rope { get; set; }
        internal MooringDockInfo Dock { get; set; }
        internal float Distance { get; set; }
    }

    internal sealed class ActiveMooringRoute
    {
        internal MooringRopeInfo Rope { get; set; }
        internal MooringDockInfo Dock { get; set; }
        internal Vector3 BoatAnchorLocal => Rope.AnchorLocal;
        internal Vector3 BoatAnchorWorld => Rope.AnchorWorld;
        internal Vector3 DockWorld => Dock.Mooring.transform.position;
    }

    internal static class MooringLocator
    {
        private const float DockSearchRadius = 90f;
        private static readonly FieldInfo SpringAnchorField =
            AccessTools.Field(typeof(PickupableBoatMooringRope), "springAnchor");

        internal static bool TryScan(out MooringScan scan)
        {
            scan = null;
            var context = CrewBoatContextResolver.Resolve();
            if (context == null)
                return false;

            var mooringRopes = context.TopBoat.GetComponent<BoatMooringRopes>()
                ?? context.WorldBoat.GetComponentInParent<BoatMooringRopes>();
            if (mooringRopes == null)
                return false;

            scan = new MooringScan
            {
                Context = context,
                MooringRopes = mooringRopes
            };

            var ropeInfos = BuildRopeInfos(context, mooringRopes);
            scan.SideMap = BuildSideMap(ropeInfos);

            foreach (var rope in ropeInfos)
            {
                rope.Side = scan.SideMap.Classify(rope.AnchorLocal);
                scan.Ropes.Add(rope);
            }

            foreach (var dock in BuildDockInfos(context, scan.SideMap))
                scan.Docks.Add(dock);

            return true;
        }

        internal static bool HasAvailableTargets(MooringSide side)
        {
            return GetAvailableRopeCount(side, null) > 0;
        }

        internal static int GetAvailableRopeCount(MooringSide side, IEnumerable<PickupableBoatMooringRope> excludedRopes)
        {
            if (!TryScan(out var scan))
                return 0;

            var excluded = new HashSet<PickupableBoatMooringRope>(excludedRopes ?? Enumerable.Empty<PickupableBoatMooringRope>());
            int freeRopes = scan.Ropes.Count(r => r.Side == side && !r.IsMoored && r.Rope != null && !excluded.Contains(r.Rope));
            int freeDocks = scan.Docks.Count(d => d.Side == side && !d.IsOccupied && d.Mooring != null);
            return Mathf.Min(freeRopes, freeDocks);
        }

        internal static bool TryFindAvailableRopes(MooringSide side, IEnumerable<PickupableBoatMooringRope> excludedRopes, out List<MooringRopeInfo> ropes)
        {
            ropes = new List<MooringRopeInfo>();
            if (!TryScan(out var scan))
                return false;

            var excluded = new HashSet<PickupableBoatMooringRope>(excludedRopes ?? Enumerable.Empty<PickupableBoatMooringRope>());
            int freeDockCount = scan.Docks.Count(d => d.Side == side && !d.IsOccupied && d.Mooring != null);
            if (freeDockCount <= 0)
                return false;

            ropes = scan.Ropes
                .Where(r => r.Side == side && !r.IsMoored && r.Rope != null && !excluded.Contains(r.Rope))
                .OrderBy(r => r.AnchorLocal.x)
                .ThenBy(r => r.AnchorLocal.z)
                .Take(freeDockCount)
                .ToList();

            return ropes.Count > 0;
        }

        internal static bool TryFindRope(PickupableBoatMooringRope targetRope, out MooringRopeInfo ropeInfo)
        {
            ropeInfo = null;
            if (!targetRope || !TryScan(out var scan))
                return false;

            ropeInfo = scan.Ropes.FirstOrDefault(r => r.Rope == targetRope);
            return ropeInfo != null;
        }

        internal static bool TryFindClosestDock(PickupableBoatMooringRope targetRope, MooringSide side, out MooringDockInfo dockInfo)
        {
            dockInfo = null;
            if (!targetRope || !TryScan(out var scan))
                return false;

            Vector3 ropeWorld = GetRopeAnchorWorld(targetRope);
            dockInfo = scan.Docks
                .Where(d => d.Side == side && !d.IsOccupied && d.Mooring != null)
                .OrderBy(d => Vector3.Distance(ropeWorld, d.Mooring.transform.position))
                .FirstOrDefault();

            return dockInfo != null;
        }

        internal static bool TryFindPairs(MooringSide side, out List<MooringPair> pairs)
        {
            pairs = new List<MooringPair>();
            if (!TryScan(out var scan))
                return false;

            var ropes = scan.Ropes
                .Where(r => r.Side == side && !r.IsMoored && r.Rope != null)
                .ToList();
            var docks = scan.Docks
                .Where(d => d.Side == side && !d.IsOccupied && d.Mooring != null)
                .ToList();

            while (ropes.Count > 0 && docks.Count > 0)
            {
                MooringPair best = null;
                foreach (var rope in ropes)
                {
                    foreach (var dock in docks)
                    {
                        float distance = Vector3.Distance(rope.AnchorWorld, dock.Mooring.transform.position);
                        if (best == null || distance < best.Distance)
                        {
                            best = new MooringPair
                            {
                                Rope = rope,
                                Dock = dock,
                                Distance = distance
                            };
                        }
                    }
                }

                if (best == null)
                    break;

                pairs.Add(best);
                ropes.Remove(best.Rope);
                docks.Remove(best.Dock);
            }

            return pairs.Count > 0;
        }

        internal static bool IsCurrentBoatMoored()
        {
            if (!TryScan(out var scan))
                return false;

            return scan.Ropes.Any(r => r.IsMoored && r.Rope != null);
        }

        internal static bool TryFindActiveRoute(Vector3 fromWorld, out ActiveMooringRoute route)
        {
            route = null;
            if (!TryScan(out var scan))
                return false;

            var mooredRopes = scan.Ropes
                .Where(r => r.IsMoored && r.Rope != null)
                .ToList();
            var occupiedDocks = scan.Docks
                .Where(d => d.IsOccupied && d.Mooring != null)
                .ToList();

            if (mooredRopes.Count == 0 || occupiedDocks.Count == 0)
                return false;

            route = mooredRopes
                .Select(r => new
                {
                    Rope = r,
                    Dock = occupiedDocks
                        .OrderBy(d => Vector3.Distance(r.Rope.transform.position, d.Mooring.transform.position))
                        .FirstOrDefault(),
                    Distance = Vector3.Distance(fromWorld, r.AnchorWorld)
                })
                .Where(x => x.Dock != null)
                .OrderBy(x => x.Distance)
                .Select(x => new ActiveMooringRoute { Rope = x.Rope, Dock = x.Dock })
                .FirstOrDefault();

            return route != null;
        }

        private static List<MooringRopeInfo> BuildRopeInfos(CrewBoatContext context, BoatMooringRopes mooringRopes)
        {
            var ropes = new List<MooringRopeInfo>();
            if (mooringRopes.ropes == null)
                return ropes;

            for (int i = 0; i < mooringRopes.ropes.Length; i++)
            {
                var rope = mooringRopes.ropes[i];
                if (!rope)
                    continue;

                Vector3 anchorWorld = GetRopeAnchorWorld(rope);
                ropes.Add(new MooringRopeInfo
                {
                    Index = i,
                    Rope = rope,
                    AnchorWorld = anchorWorld,
                    AnchorLocal = context.WorldBoat.InverseTransformPoint(anchorWorld),
                    CurrentLocal = context.WorldBoat.InverseTransformPoint(rope.transform.position),
                    IsMoored = rope.IsMoored()
                });
            }

            return ropes;
        }

        private static List<MooringDockInfo> BuildDockInfos(CrewBoatContext context, MooringSideMap sideMap)
        {
            Vector3 boatPosition = context.TopBoat.position;
            return Object.FindObjectsOfType<GPButtonDockMooring>()
                .Where(d => d && d.gameObject.activeInHierarchy)
                .Where(d => !IsBoatOwnedDock(context, d))
                .Select((d, i) =>
                {
                    Vector3 local = context.WorldBoat.InverseTransformPoint(d.transform.position);
                    return new MooringDockInfo
                    {
                        Index = i,
                        Mooring = d,
                        LocalPosition = local,
                        DistanceToBoat = Vector3.Distance(boatPosition, d.transform.position),
                        IsOccupied = d.spring != null && d.spring.connectedBody != null,
                        Side = sideMap.Classify(local)
                    };
                })
                .Where(d => d.DistanceToBoat <= DockSearchRadius)
                .OrderBy(d => d.DistanceToBoat)
                .Select((d, i) =>
                {
                    d.Index = i;
                    return d;
                })
                .ToList();
        }

        private static bool IsBoatOwnedDock(CrewBoatContext context, GPButtonDockMooring dock)
        {
            if (context == null || dock == null)
                return true;

            Transform dockTransform = dock.transform;
            return (context.TopBoat && dockTransform.IsChildOf(context.TopBoat))
                || (context.WorldBoat && dockTransform.IsChildOf(context.WorldBoat));
        }

        private static MooringSideMap BuildSideMap(List<MooringRopeInfo> ropes)
        {
            MooringBeamAxis axis = MooringBeamAxis.LocalX;
            bool portPositive = false;

            if (ropes.Count >= 2)
            {
                float xRange = ropes.Max(r => r.AnchorLocal.x) - ropes.Min(r => r.AnchorLocal.x);
                float zRange = ropes.Max(r => r.AnchorLocal.z) - ropes.Min(r => r.AnchorLocal.z);
                axis = zRange < xRange ? MooringBeamAxis.LocalZ : MooringBeamAxis.LocalX;
            }

            var leftSamples = ropes
                .Where(r => r.Rope.name.ToLowerInvariant().Contains("left"))
                .Select(r => AxisValue(axis, r.AnchorLocal))
                .ToList();
            var rightSamples = ropes
                .Where(r => r.Rope.name.ToLowerInvariant().Contains("right"))
                .Select(r => AxisValue(axis, r.AnchorLocal))
                .ToList();

            if (leftSamples.Count > 0 && rightSamples.Count > 0)
                portPositive = leftSamples.Average() > rightSamples.Average();
            else
                portPositive = axis == MooringBeamAxis.LocalZ;

            return new MooringSideMap(axis, portPositive);
        }

        private static float AxisValue(MooringBeamAxis axis, Vector3 local)
        {
            return axis == MooringBeamAxis.LocalX ? local.x : local.z;
        }

        private static Vector3 GetRopeAnchorWorld(PickupableBoatMooringRope rope)
        {
            var boatRigidbody = rope.GetBoatRigidbody();
            if (boatRigidbody != null && SpringAnchorField != null)
            {
                object value = SpringAnchorField.GetValue(rope);
                if (value is Vector3 springAnchor)
                    return boatRigidbody.transform.TransformPoint(springAnchor);
            }

            return rope.transform.position;
        }
    }
}
