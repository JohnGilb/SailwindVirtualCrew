using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

namespace SailwindVirtualCrew
{
    // Island scenery meshes aren't CPU-readable, so the NavMesh builder skips them. Physics can still
    // raycast them, so sample each collider on a world-aligned grid and rebuild it as solid boxes.
    // Runs a few colliders per frame; boxes are stored relative to the port so a world shift mid-way can't skew them.
    internal sealed class ColliderStandInBuilder
    {
        private const float BaseStep = 0.25f;
        private const int MaxCellsPerCollider = 6000;
        private const int MaxHitsPerColumn = 8;
        private const float Skin = 0.01f;
        private const float MinThickness = 0.05f;
        private const float MergeTolerance = 0.1f;

        private readonly List<MeshCollider> _colliders;
        private readonly Transform _frame;
        private readonly Bounds _localArea;
        private readonly Stopwatch _timer = new Stopwatch();
        private int _next;

        internal List<NavMeshBuildSource> Sources { get; } = new List<NavMeshBuildSource>();
        internal int ColliderCount => _colliders.Count;
        internal int Rays { get; private set; }
        internal int Cells { get; private set; }
        internal long ElapsedMilliseconds => _timer.ElapsedMilliseconds;
        internal bool IsDone => _next >= _colliders.Count;

        private struct Span
        {
            internal float Bottom;
            internal float Top;
        }

        internal ColliderStandInBuilder(List<MeshCollider> colliders, Transform frame, Bounds localArea)
        {
            _colliders = colliders;
            _frame = frame;
            _localArea = localArea;
        }

        // Works through colliders until the budget is spent; true once all are done.
        internal bool Step(float budgetMilliseconds)
        {
            if (IsDone || !_frame)
                return true;

            var slice = Stopwatch.StartNew();
            _timer.Start();
            Bounds area = WorldArea();
            while (!IsDone && slice.Elapsed.TotalMilliseconds < budgetMilliseconds)
            {
                var collider = _colliders[_next++];
                if (collider)
                    Sample(collider, area);
            }
            _timer.Stop();
            return IsDone;
        }

        // The frame is used for its position only (see ShoreNavMesh).
        private Bounds WorldArea()
        {
            return new Bounds(_localArea.center + _frame.position, _localArea.size);
        }

        private void Sample(Collider collider, Bounds area)
        {
            Bounds b = collider.bounds;
            float minX = Mathf.Max(b.min.x, area.min.x), maxX = Mathf.Min(b.max.x, area.max.x);
            float minZ = Mathf.Max(b.min.z, area.min.z), maxZ = Mathf.Min(b.max.z, area.max.z);
            if (maxX <= minX || maxZ <= minZ)
                return;

            float step = BaseStep;
            int nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / step));
            int nz = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / step));
            if (nx * nz > MaxCellsPerCollider)
            {
                step *= Mathf.Sqrt((float)(nx * nz) / MaxCellsPerCollider);
                nx = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / step));
                nz = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / step));
            }

            float top = b.max.y + 0.5f;
            float bottom = b.min.y - 0.5f;
            Matrix4x4 toFrame = Matrix4x4.Translate(-_frame.position);
            for (int iz = 0; iz < nz; iz++)
            {
                float z = minZ + (iz + 0.5f) * step;
                List<Span> runSpans = null;
                int runStart = 0;
                for (int ix = 0; ix <= nx; ix++)
                {
                    List<Span> spans = null;
                    if (ix < nx)
                    {
                        spans = SampleColumn(collider, minX + (ix + 0.5f) * step, z, top, bottom, b.min.y);
                        Cells++;
                        if (runSpans != null && SameSpans(spans, runSpans))
                            continue;
                    }

                    if (runSpans != null)
                        AddRun(toFrame, runSpans, minX + runStart * step, minX + ix * step, z, step);
                    runSpans = spans;
                    runStart = ix;
                }
            }
        }

        // Downward rays find the up-facing surfaces and upward rays the down-facing ones (back faces aren't hit).
        // Each up-facing surface is solid down to the nearest down-facing surface below it, or the collider's bottom.
        private List<Span> SampleColumn(Collider collider, float x, float z, float top, float bottom, float colliderBottom)
        {
            var ups = new List<float>();
            var origin = new Vector3(x, top, z);
            for (int i = 0; i < MaxHitsPerColumn; i++)
            {
                Rays++;
                if (!collider.Raycast(new Ray(origin, Vector3.down), out var hit, origin.y - bottom))
                    break;
                ups.Add(hit.point.y);
                origin.y = hit.point.y - Skin;
            }

            var spans = new List<Span>();
            if (ups.Count == 0)
                return spans;

            var downs = new List<float>();
            origin = new Vector3(x, bottom, z);
            for (int i = 0; i < MaxHitsPerColumn; i++)
            {
                Rays++;
                if (!collider.Raycast(new Ray(origin, Vector3.up), out var hit, top - origin.y))
                    break;
                downs.Add(hit.point.y);
                origin.y = hit.point.y + Skin;
            }

            foreach (float up in ups)
            {
                float below = colliderBottom;
                foreach (float down in downs)
                    if (down < up - Skin && down > below)
                        below = down;

                if (up - below < MinThickness)
                    below = up - MinThickness;
                spans.Add(new Span { Bottom = below, Top = up });
            }

            spans.Sort((a, c) => a.Bottom.CompareTo(c.Bottom));
            var merged = new List<Span>();
            foreach (var span in spans)
            {
                if (merged.Count > 0 && span.Bottom <= merged[merged.Count - 1].Top + Skin)
                {
                    var last = merged[merged.Count - 1];
                    last.Top = Mathf.Max(last.Top, span.Top);
                    merged[merged.Count - 1] = last;
                }
                else
                {
                    merged.Add(span);
                }
            }
            return merged;
        }

        private static bool SameSpans(List<Span> a, List<Span> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (Mathf.Abs(a[i].Bottom - b[i].Bottom) > MergeTolerance || Mathf.Abs(a[i].Top - b[i].Top) > MergeTolerance)
                    return false;
            return true;
        }

        private void AddRun(Matrix4x4 toFrame, List<Span> spans, float x0, float x1, float z, float step)
        {
            foreach (var span in spans)
            {
                var center = new Vector3((x0 + x1) * 0.5f, (span.Bottom + span.Top) * 0.5f, z);
                Sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = new Vector3(x1 - x0, span.Top - span.Bottom, step),
                    transform = toFrame * Matrix4x4.Translate(center),
                    area = 0
                });
            }
        }
    }
}
