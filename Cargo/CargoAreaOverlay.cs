using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Draws the painted cargo area on the real (visible) boat: a tinted tile on the floor of each free run, shading
    /// from orange (low headroom) to green (standing room), so a sloped hull shows as tiles stepping up its side.
    /// Where the fill was turned down, the painted cell gets a strip along that edge, coloured by the reason, until the
    /// area is cleared: red where structure blocks the way, purple where there is no floor, yellow where headroom is
    /// too low, blue at a ledge. The strip is drawn on the painted side because the rejected side may be inside a
    /// beam or pillar. The mesh is in the world boat's local frame, which matches the walk collider's local frame the
    /// area is stored in.
    /// </summary>
    internal static class CargoAreaOverlay
    {
        private const string ObjectName = "VC_CargoAreaOverlay";
        private const float TileLift = 0.02f;
        private const float TileInset = 0.08f;
        private const float EdgeStripWidth = 0.3f;
        private const float LowHeadroom = 0.3f;
        private const float HighHeadroom = 1.2f;

        private static readonly Color LowColor = new Color(1f, 0.55f, 0.1f, 0.45f);
        private static readonly Color HighColor = new Color(0.2f, 0.9f, 0.35f, 0.45f);
        private static readonly Color BlockedColor = new Color(0.95f, 0.1f, 0.1f, 0.55f);
        private static readonly Color NoFloorColor = new Color(0.7f, 0.2f, 0.95f, 0.55f);
        private static readonly Color LowHeadroomColor = new Color(1f, 0.9f, 0.1f, 0.55f);
        private static readonly Color LedgeColor = new Color(0.2f, 0.5f, 1f, 0.55f);

        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<Color> Colors = new List<Color>();
        private static readonly List<int> Triangles = new List<int>();

        private static GameObject overlay;
        private static Mesh mesh;
        private static CargoArea builtArea;
        private static int builtAreaVersion = -1;
        private static int builtRejectedVersion = -1;

        internal static void Update(CrewBoatContext context, CargoArea area)
        {
            if (context == null || area == null || !context.WorldBoat)
            {
                Hide();
                return;
            }

            EnsureOverlay(context.WorldBoat);
            if (!overlay.activeSelf)
                overlay.SetActive(true);

            if (builtArea != area
                || builtAreaVersion != area.Version
                || builtRejectedVersion != CargoAreaPainter.RejectedVersion)
                Rebuild(area);
        }

        internal static void Hide()
        {
            if (overlay && overlay.activeSelf)
                overlay.SetActive(false);
        }

        private static void EnsureOverlay(Transform worldBoat)
        {
            if (!overlay)
            {
                overlay = new GameObject(ObjectName);
                overlay.layer = 2;
                mesh = new Mesh { name = ObjectName };
                mesh.MarkDynamic();
                overlay.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = overlay.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                builtArea = null;
            }

            if (overlay.transform.parent != worldBoat)
            {
                overlay.transform.SetParent(worldBoat, false);
                overlay.transform.localPosition = Vector3.zero;
                overlay.transform.localRotation = Quaternion.identity;
                overlay.transform.localScale = Vector3.one;
            }
        }

        private static void Rebuild(CargoArea area)
        {
            Vertices.Clear();
            Colors.Clear();
            Triangles.Clear();

            foreach (var painted in area.EnumerateRuns())
            {
                float t = Mathf.InverseLerp(LowHeadroom, HighHeadroom, painted.Run.Height);
                AddTile(painted.Ix, painted.Iz, painted.Run.FloorY, Color.Lerp(LowColor, HighColor, t));
            }

            foreach (var rejected in CargoAreaPainter.RejectedMarks)
                AddEdgeStrip(rejected.Ix, rejected.Iz, rejected.Dir, rejected.Y, ReasonColor(rejected.Reason));

            mesh.Clear();
            mesh.indexFormat = Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(Vertices);
            mesh.SetColors(Colors);
            mesh.SetTriangles(Triangles, 0);
            mesh.RecalculateBounds();

            builtArea = area;
            builtAreaVersion = area.Version;
            builtRejectedVersion = CargoAreaPainter.RejectedVersion;
        }

        private static Color ReasonColor(CargoRejectReason reason)
        {
            switch (reason)
            {
                case CargoRejectReason.NoFloor: return NoFloorColor;
                case CargoRejectReason.LowHeadroom: return LowHeadroomColor;
                case CargoRejectReason.Ledge: return LedgeColor;
                default: return BlockedColor;
            }
        }

        private static void AddTile(int ix, int iz, float floorY, Color color)
        {
            float size = CargoArea.CellSize;
            float inset = TileInset * size;
            AddQuad(
                ix * size + inset,
                (ix + 1) * size - inset,
                iz * size + inset,
                (iz + 1) * size - inset,
                floorY + TileLift,
                color);
        }

        // A strip along one edge of a painted cell, facing the neighbour the fill turned down, just above the tile.
        private static void AddEdgeStrip(int ix, int iz, int dir, float floorY, Color color)
        {
            float size = CargoArea.CellSize;
            float x0 = ix * size, x1 = x0 + size, z0 = iz * size, z1 = z0 + size;
            float strip = size * EdgeStripWidth;
            switch (dir)
            {
                case 0: x0 = x1 - strip; break;
                case 1: x1 = x0 + strip; break;
                case 2: z0 = z1 - strip; break;
                default: z1 = z0 + strip; break;
            }

            AddQuad(x0, x1, z0, z1, floorY + TileLift + 0.005f, color);
        }

        private static void AddQuad(float x0, float x1, float z0, float z1, float y, Color color)
        {
            int start = Vertices.Count;
            Vertices.Add(new Vector3(x0, y, z0));
            Vertices.Add(new Vector3(x0, y, z1));
            Vertices.Add(new Vector3(x1, y, z1));
            Vertices.Add(new Vector3(x1, y, z0));
            for (int i = 0; i < 4; i++)
                Colors.Add(color);

            Triangles.Add(start);
            Triangles.Add(start + 1);
            Triangles.Add(start + 2);
            Triangles.Add(start);
            Triangles.Add(start + 2);
            Triangles.Add(start + 3);
        }
    }
}
