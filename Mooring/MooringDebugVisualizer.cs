using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class MooringDebugVisualizer
    {
        private const string Phase = "MooringDebug";
        private const string Prefix = "VC_Debug_Mooring_";
        private const float DockSearchRadius = 90f;
        private const float CenterlineThreshold = 0.15f;

        private static readonly FieldInfo SpringAnchorField =
            AccessTools.Field(typeof(PickupableBoatMooringRope), "springAnchor");

        private static readonly List<GameObject> Markers = new List<GameObject>();

        private enum BoatSide
        {
            Center,
            Port,
            Starboard
        }

        private enum BeamAxis
        {
            LocalX,
            LocalZ
        }

        internal static void DumpScan()
        {
            var scan = BuildScan(logFailures: true);
            if (scan == null)
                return;

            CrewDebugLog.Ok(Phase,
                "Mooring scan boat='" + scan.Context.TopBoat.name
                + "' ropes=" + scan.Ropes.Count
                + " docksNear=" + scan.Docks.Count
                + " beamAxis=" + scan.SideMap.Axis
                + " portSign=" + (scan.SideMap.PortPositive ? "+" : "-")
                + " radius=" + DockSearchRadius.ToString("0") + "m");

            foreach (var rope in scan.Ropes)
            {
                CrewDebugLog.Ok(Phase,
                    "rope[" + rope.Index + "] side=" + rope.Side
                    + " moored=" + rope.IsMoored
                    + " sideCoord=" + scan.SideMap.AxisValue(rope.AnchorLocal).ToString("0.000")
                    + " anchorLocal=" + Format(rope.AnchorLocal)
                    + " currentLocal=" + Format(rope.CurrentLocal)
                    + " name='" + rope.Rope.name + "'");
            }

            foreach (var dock in scan.Docks.Take(32))
            {
                CrewDebugLog.Ok(Phase,
                    "dock[" + dock.Index + "] side=" + dock.Side
                    + " occupied=" + dock.IsOccupied
                    + " sideCoord=" + scan.SideMap.AxisValue(dock.LocalPosition).ToString("0.000")
                    + " distance=" + dock.DistanceToBoat.ToString("0.0") + "m"
                    + " local=" + Format(dock.LocalPosition)
                    + " name='" + dock.Mooring.name + "'");
            }
        }

        internal static void ShowMarkers()
        {
            ClearMarkers();

            var scan = BuildScan(logFailures: true);
            if (scan == null)
                return;

            CreateSideLabels(scan);

            foreach (var rope in scan.Ropes)
            {
                Color color = SideColor(rope.Side);
                var marker = CreatePrimitiveMarker(
                    Prefix + "RopeAnchor_" + rope.Index,
                    PrimitiveType.Sphere,
                    scan.Context.WorldBoat,
                    rope.AnchorLocal,
                    Quaternion.identity,
                    Vector3.one * 0.42f,
                    color);

                AddLabel(
                    marker.transform,
                    (rope.Side == BoatSide.Port ? "PORT" : rope.Side == BoatSide.Starboard ? "STARBOARD" : "CENTER")
                    + " rope " + rope.Index
                    + "\n" + scan.SideMap.AxisName + "=" + scan.SideMap.AxisValue(rope.AnchorLocal).ToString("0.00")
                    + (rope.IsMoored ? "\nmoored" : "\nfree"),
                    color,
                    Vector3.up * 0.75f);

                if (Vector3.Distance(rope.AnchorWorld, rope.Rope.transform.position) > 0.4f)
                    CreateLine(Prefix + "RopeCurrentLine_" + rope.Index, rope.AnchorWorld, rope.Rope.transform.position, color);
            }

            foreach (var dock in scan.Docks)
            {
                Color color = dock.IsOccupied ? Color.gray : Color.yellow;
                var marker = CreatePrimitiveMarker(
                    Prefix + "Dock_" + dock.Index,
                    PrimitiveType.Cylinder,
                    dock.Mooring.transform,
                    Vector3.zero,
                    Quaternion.identity,
                    new Vector3(0.42f, 0.12f, 0.42f),
                    color);

                AddLabel(
                    marker.transform,
                    "dock " + dock.Index
                    + "\n" + dock.Side
                    + "\n" + (dock.IsOccupied ? "occupied" : "open"),
                    color,
                    Vector3.up * 0.8f);
            }

            CrewDebugLog.Ok(Phase,
                "Created mooring markers ropes=" + scan.Ropes.Count
                + " docks=" + scan.Docks.Count
                + ". Color key: port=red, starboard=green, dock=open yellow, dock occupied gray.");
        }

        internal static void ClearMarkers()
        {
            foreach (var marker in Markers)
            {
                if (marker)
                    Object.Destroy(marker);
            }
            Markers.Clear();

            var transforms = Object.FindObjectsOfType<Transform>();
            foreach (var transform in transforms)
            {
                if (transform && transform.name.StartsWith(Prefix))
                    Object.Destroy(transform.gameObject);
            }

            CrewDebugLog.Ok(Phase, "Cleared mooring debug markers.");
        }

        private static MooringScan BuildScan(bool logFailures)
        {
            var context = CrewBoatContextResolver.ResolveAndLog();
            if (context == null)
                return null;

            var mooringRopes = context.TopBoat.GetComponent<BoatMooringRopes>()
                ?? context.WorldBoat.GetComponentInParent<BoatMooringRopes>();

            if (mooringRopes == null)
            {
                if (logFailures)
                    CrewDebugLog.Warn(Phase, "Current boat has no BoatMooringRopes component.");
                return null;
            }

            var scan = new MooringScan
            {
                Context = context,
                MooringRopes = mooringRopes
            };

            var anchors = new List<RopeAnchor>();
            if (mooringRopes.ropes != null)
            {
                for (int i = 0; i < mooringRopes.ropes.Length; i++)
                {
                    var rope = mooringRopes.ropes[i];
                    if (!rope)
                        continue;

                    Vector3 anchorWorld = GetRopeAnchorWorld(rope);
                    anchors.Add(new RopeAnchor
                    {
                        Index = i,
                        Rope = rope,
                        AnchorWorld = anchorWorld,
                        AnchorLocal = context.WorldBoat.InverseTransformPoint(anchorWorld),
                        CurrentLocal = context.WorldBoat.InverseTransformPoint(rope.transform.position)
                    });
                }
            }

            scan.SideMap = BuildSideMap(context, anchors);

            foreach (var anchor in anchors)
            {
                scan.Ropes.Add(new RopeInfo
                {
                    Index = anchor.Index,
                    Rope = anchor.Rope,
                    AnchorWorld = anchor.AnchorWorld,
                    AnchorLocal = anchor.AnchorLocal,
                    CurrentLocal = anchor.CurrentLocal,
                    Side = scan.SideMap.Classify(anchor.AnchorLocal),
                    IsMoored = anchor.Rope.IsMoored()
                });
            }

            Vector3 boatPosition = context.TopBoat.position;
            var dockCandidates = Object.FindObjectsOfType<GPButtonDockMooring>()
                .Where(d => d && d.gameObject.activeInHierarchy)
                .Select((d, i) => new DockInfo
                {
                    Index = i,
                    Mooring = d,
                    LocalPosition = context.WorldBoat.InverseTransformPoint(d.transform.position),
                    DistanceToBoat = Vector3.Distance(boatPosition, d.transform.position),
                    IsOccupied = d.spring != null && d.spring.connectedBody != null
                })
                .Where(d => d.DistanceToBoat <= DockSearchRadius)
                .OrderBy(d => d.DistanceToBoat)
                .ToList();

            for (int i = 0; i < dockCandidates.Count; i++)
            {
                dockCandidates[i].Index = i;
                dockCandidates[i].Side = scan.SideMap.Classify(dockCandidates[i].LocalPosition);
                scan.Docks.Add(dockCandidates[i]);
            }

            return scan;
        }

        private static SideMap BuildSideMap(CrewBoatContext context, List<RopeAnchor> anchors)
        {
            BeamAxis axis = BeamAxis.LocalX;
            bool portPositive = false;

            if (anchors.Count >= 2)
            {
                float xRange = anchors.Max(a => a.AnchorLocal.x) - anchors.Min(a => a.AnchorLocal.x);
                float zRange = anchors.Max(a => a.AnchorLocal.z) - anchors.Min(a => a.AnchorLocal.z);

                axis = zRange < xRange ? BeamAxis.LocalZ : BeamAxis.LocalX;
            }

            var leftSamples = anchors
                .Where(a => a.Rope.name.ToLowerInvariant().Contains("left"))
                .Select(a => AxisValue(axis, a.AnchorLocal))
                .ToList();

            var rightSamples = anchors
                .Where(a => a.Rope.name.ToLowerInvariant().Contains("right"))
                .Select(a => AxisValue(axis, a.AnchorLocal))
                .ToList();

            if (leftSamples.Count > 0 && rightSamples.Count > 0)
                portPositive = leftSamples.Average() > rightSamples.Average();
            else
                portPositive = axis == BeamAxis.LocalZ;

            if (IsCog(context))
                portPositive = !portPositive;

            return new SideMap(axis, portPositive);
        }

        private static bool IsCog(CrewBoatContext context)
        {
            string topName = context != null && context.TopBoat ? context.TopBoat.name.ToLowerInvariant() : "";
            string worldName = context != null && context.WorldBoat ? context.WorldBoat.name.ToLowerInvariant() : "";
            return topName.Contains("medi small") || worldName.Contains("medi small") || topName.Contains("cog") || worldName.Contains("cog");
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

        private static float AxisValue(BeamAxis axis, Vector3 local)
        {
            return axis == BeamAxis.LocalX ? local.x : local.z;
        }

        private static Color SideColor(BoatSide side)
        {
            switch (side)
            {
                case BoatSide.Port:
                    return Color.red;
                case BoatSide.Starboard:
                    return Color.green;
                default:
                    return Color.white;
            }
        }

        private static void CreateSideLabels(MooringScan scan)
        {
            float foreAft = 0f;
            float beamExtent = 2.5f;
            if (scan.Ropes.Count > 0)
            {
                if (scan.SideMap.Axis == BeamAxis.LocalX)
                {
                    foreAft = scan.Ropes.Average(r => r.AnchorLocal.z);
                    beamExtent = Mathf.Max(2.5f, scan.Ropes.Max(r => Mathf.Abs(r.AnchorLocal.x)) + 1.25f);
                    CreateSideLabel(scan.Context.WorldBoat, new Vector3(scan.SideMap.PortPositive ? beamExtent : -beamExtent, 1.8f, foreAft), scan.SideMap.PortLabel, Color.red);
                    CreateSideLabel(scan.Context.WorldBoat, new Vector3(scan.SideMap.PortPositive ? -beamExtent : beamExtent, 1.8f, foreAft), scan.SideMap.StarboardLabel, Color.green);
                    return;
                }

                foreAft = scan.Ropes.Average(r => r.AnchorLocal.x);
                beamExtent = Mathf.Max(2.5f, scan.Ropes.Max(r => Mathf.Abs(r.AnchorLocal.z)) + 1.25f);
            }

            CreateSideLabel(scan.Context.WorldBoat, new Vector3(foreAft, 1.8f, scan.SideMap.PortPositive ? beamExtent : -beamExtent), scan.SideMap.PortLabel, Color.red);
            CreateSideLabel(scan.Context.WorldBoat, new Vector3(foreAft, 1.8f, scan.SideMap.PortPositive ? -beamExtent : beamExtent), scan.SideMap.StarboardLabel, Color.green);
        }

        private static void CreateSideLabel(Transform parent, Vector3 localPosition, string text, Color color)
        {
            var root = new GameObject(Prefix + text.Replace(" ", "_"));
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPosition;
            root.transform.localRotation = Quaternion.identity;
            Markers.Add(root);

            AddLabel(root.transform, text, color, Vector3.zero, 0.28f);
        }

        private static GameObject CreatePrimitiveMarker(
            string name,
            PrimitiveType primitive,
            Transform parent,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale,
            Color color)
        {
            var marker = GameObject.CreatePrimitive(primitive);
            marker.name = name;
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = localPosition;
            marker.transform.localRotation = localRotation;
            marker.transform.localScale = localScale;

            var collider = marker.GetComponent<Collider>();
            if (collider)
                Object.Destroy(collider);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = color;

            Markers.Add(marker);
            return marker;
        }

        private static void AddLabel(Transform parent, string text, Color color, Vector3 localOffset, float characterSize = 0.13f)
        {
            var labelObject = new GameObject(Prefix + "Label");
            labelObject.transform.SetParent(parent, false);
            labelObject.transform.localPosition = localOffset;
            labelObject.transform.localRotation = Quaternion.identity;

            var textMesh = labelObject.AddComponent<TextMesh>();
            textMesh.text = text;
            textMesh.color = color;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.characterSize = characterSize;
            textMesh.fontSize = 48;

            labelObject.AddComponent<MooringDebugBillboard>();
            Markers.Add(labelObject);
        }

        private static void CreateLine(string name, Vector3 fromWorld, Vector3 toWorld, Color color)
        {
            var lineObject = new GameObject(name);
            var line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, fromWorld);
            line.SetPosition(1, toWorld);
            line.startWidth = 0.05f;
            line.endWidth = 0.05f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = color;
            Markers.Add(lineObject);
        }

        private static string Format(Vector3 value)
        {
            return "(" + value.x.ToString("0.000") + ", " + value.y.ToString("0.000") + ", " + value.z.ToString("0.000") + ")";
        }

        private sealed class SideMap
        {
            internal readonly BeamAxis Axis;
            internal readonly bool PortPositive;

            internal SideMap(BeamAxis axis, bool portPositive)
            {
                Axis = axis;
                PortPositive = portPositive;
            }

            internal string AxisName => Axis == BeamAxis.LocalX ? "X" : "Z";
            internal string PortLabel => "PORT (" + (PortPositive ? "+" : "-") + AxisName + ")";
            internal string StarboardLabel => "STARBOARD (" + (PortPositive ? "-" : "+") + AxisName + ")";

            internal float AxisValue(Vector3 local)
            {
                return MooringDebugVisualizer.AxisValue(Axis, local);
            }

            internal BoatSide Classify(Vector3 local)
            {
                float value = AxisValue(local);
                if (Mathf.Abs(value) <= CenterlineThreshold)
                    return BoatSide.Center;

                return (value > 0f) == PortPositive ? BoatSide.Port : BoatSide.Starboard;
            }
        }

        private sealed class MooringScan
        {
            internal CrewBoatContext Context;
            internal BoatMooringRopes MooringRopes;
            internal SideMap SideMap;
            internal readonly List<RopeInfo> Ropes = new List<RopeInfo>();
            internal readonly List<DockInfo> Docks = new List<DockInfo>();
        }

        private sealed class RopeAnchor
        {
            internal int Index;
            internal PickupableBoatMooringRope Rope;
            internal Vector3 AnchorWorld;
            internal Vector3 AnchorLocal;
            internal Vector3 CurrentLocal;
        }

        private sealed class RopeInfo
        {
            internal int Index;
            internal PickupableBoatMooringRope Rope;
            internal Vector3 AnchorWorld;
            internal Vector3 AnchorLocal;
            internal Vector3 CurrentLocal;
            internal BoatSide Side;
            internal bool IsMoored;
        }

        private sealed class DockInfo
        {
            internal int Index;
            internal GPButtonDockMooring Mooring;
            internal Vector3 LocalPosition;
            internal float DistanceToBoat;
            internal bool IsOccupied;
            internal BoatSide Side;
        }
    }

    internal sealed class MooringDebugBillboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (Camera.main)
                transform.rotation = Camera.main.transform.rotation;
        }
    }
}
