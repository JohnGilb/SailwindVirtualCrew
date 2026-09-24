using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal enum CargoPaintMode
    {
        Off,
        Paint,
        Erase
    }

    internal enum CargoRejectReason
    {
        // Structure fills the column where the neighbouring space would continue (pillar, hull, bulkhead).
        Blocked,
        // Free space continues, but nothing below it inside the band: a hole, a hatch, or outside the hull.
        NoFloor,
        // Free space with a floor, but too little headroom for cargo.
        LowHeadroom,
        // Free space with a floor, but the floor jumps further than a step (a tabletop or ledge).
        Ledge
    }

    /// <summary>
    /// Developer spike: the player "paints" a cargo area by walking through it. A few times a second, free space is
    /// flood-filled outwards from the player's feet, out to the brush radius. Each 10cm column in a height band around
    /// the player's floor is split into free runs: stretches of the column that no boat structure touches. A run is
    /// kept when it rests on structure, has enough headroom, and connects to an already-kept run in a neighbouring
    /// column (overlapping vertically, with floors no more than a step apart). Because the fill only spreads through
    /// kept runs, it cannot leak through the hull or a bulkhead, and it follows sloped hulls and steps over low beams.
    ///
    /// Whether each 10cm voxel of a column contains structure is probed once, by splitting the column in halves until
    /// the structure is found, and cached for the session. All queries run against the walk collider, the static copy
    /// of the boat the player walks in, and only its structural colliders count: loose items and the player are ignored.
    /// </summary>
    internal static class CargoAreaPainter
    {
        private const string Phase = "CargoPaint";

        private const float StampInterval = 0.1f;
        private const float VoxelSize = CargoArea.CellSize;
        // The band of each column examined reaches this far below the player's floor.
        private const float BandBelow = 0.25f;
        private const float MinHeadroom = 0.3f;
        // How far above the player's floor the run under the player may start.
        private const float StartStepAllowance = 0.35f;
        // Shrink each probe box so it doesn't touch the neighbouring column or voxel.
        private const float HorizontalHalfExtent = VoxelSize * 0.49f;
        private const float VerticalInset = 0.001f;
        private const float MinBrushRadius = 0.3f;
        private const float MaxBrushRadius = 3f;
        private const float MinHeightCap = 0.5f;
        private const float MaxHeightCap = 3f;
        // Voxels tracked per cached column; must cover the band (BandBelow + MaxHeightCap) with room to spare.
        private const int ColumnCacheVoxels = 64;
        private const int ColumnCacheMargin = 12;

        /// <summary>
        /// A boundary the fill could not cross: from painted cell (Ix, Iz) towards its neighbour in direction Dir
        /// (0 +x, 1 -x, 2 +z, 3 -z). Drawn on the painted side, since the rejected side may be inside structure.
        /// </summary>
        internal struct RejectedMark
        {
            public int Ix;
            public int Iz;
            public int Dir;
            public float Y;
            public CargoRejectReason Reason;
        }

        // What is known about the structure in one column, plus the runs last built from it for a given band.
        private sealed class ColumnCache
        {
            public int BaseIy;
            public ulong Known;
            public ulong Blocked;
            public int RunsBandLo = int.MinValue;
            public int RunsBandHi = int.MinValue;
            public readonly List<ColumnRun> Runs = new List<ColumnRun>(2);
        }

        private struct ColumnRun
        {
            public int Lo;
            public int Hi;
            public bool Supported;
            public CargoArea.Run Run;
        }

        private struct FrontierRun
        {
            public int Ix;
            public int Iz;
            public CargoArea.Run Run;
        }

        // Areas by vessel key (the same key as the rest of the per-vessel save data), loaded from the save on first use.
        private static readonly Dictionary<string, CargoArea> Areas = new Dictionary<string, CargoArea>();
        private static readonly HashSet<Collider> BoatColliders = new HashSet<Collider>();
        private static readonly Dictionary<long, ColumnCache> Columns = new Dictionary<long, ColumnCache>();
        private static readonly Dictionary<long, RejectedMark> Rejected = new Dictionary<long, RejectedMark>();
        private static readonly RaycastHit[] HitBuffer = new RaycastHit[64];
        private static readonly Collider[] OverlapBuffer = new Collider[64];
        private static readonly HashSet<long> Visited = new HashSet<long>();
        private static readonly Queue<FrontierRun> Frontier = new Queue<FrontierRun>();

        private static Transform collidersWalkCol;
        private static float nextStampTime;
        private static float brushRadius = 1f;
        private static float heightCap = 1.5f;
        private static int stampQueries;

        internal static CargoPaintMode Mode { get; private set; } = CargoPaintMode.Off;
        internal static bool ShowOverlay { get; set; } = true;
        internal static IEnumerable<RejectedMark> RejectedMarks => Rejected.Values;
        internal static int RejectedCount => Rejected.Count;
        internal static int RejectedVersion { get; private set; }
        internal static float LastStampMilliseconds { get; private set; }
        internal static int LastStampQueries { get; private set; }
        internal static int LastStampRejected { get; private set; }
        internal static int CachedColumnCount => Columns.Count;
        internal static string LastProblem { get; private set; }

        internal static float BrushRadius
        {
            get => brushRadius;
            set => brushRadius = Mathf.Clamp(value, MinBrushRadius, MaxBrushRadius);
        }

        /// <summary>How far above the player's floor free space is painted where there is no ceiling.</summary>
        internal static float HeightCap
        {
            get => heightCap;
            set => heightCap = Mathf.Clamp(value, MinHeightCap, MaxHeightCap);
        }

        internal static void SetMode(CargoPaintMode mode)
        {
            if (Mode == mode)
                return;

            Mode = mode;
            // Pick up the boat's colliders (and so re-probe its structure) afresh each session, in case parts or
            // hatches changed.
            collidersWalkCol = null;
            NotificationUi.instance?.ShowNotification("Cargo painting: " + mode);
        }

        internal static void CycleMode()
        {
            SetMode(Mode == CargoPaintMode.Off ? CargoPaintMode.Paint
                : Mode == CargoPaintMode.Paint ? CargoPaintMode.Erase
                : CargoPaintMode.Off);
        }

        internal static CargoArea GetActiveArea()
        {
            var context = CrewBoatContextResolver.Resolve();
            return context != null ? GetArea(context) : null;
        }

        internal static void ClearActiveArea()
        {
            GetActiveArea()?.Clear();
            ClearRejected();
        }

        internal static void LogActiveArea()
        {
            var context = CrewBoatContextResolver.Resolve();
            var area = context != null ? GetArea(context) : null;
            if (area == null)
            {
                CrewDebugLog.Warn(Phase, "No active vessel; nothing to log.");
                return;
            }

            var islands = area.FindIslands();
            CrewDebugLog.Ok(Phase,
                "Cargo area boat='" + context.TopBoat.name
                + "' columns=" + area.ColumnCount
                + " runs=" + area.RunCount
                + " floor=" + area.FloorAreaSquareMeters.ToString("0.00") + "m2"
                + " volume=" + area.VolumeCubicMeters.ToString("0.00") + "m3"
                + " islands=" + islands.Count
                + " boatColliders=" + BoatColliders.Count
                + " cachedColumns=" + Columns.Count
                + " rejected=" + Rejected.Count);

            for (int i = 0; i < islands.Count; i++)
            {
                var island = islands[i];
                CrewDebugLog.Ok(Phase,
                    "island[" + i + "] columns=" + island.ColumnCount
                    + " volume=" + island.Volume.ToString("0.00") + "m3"
                    + " min=" + Format(island.Min)
                    + " max=" + Format(island.Max)
                    + " height=" + island.MinHeight.ToString("0.00") + ".." + island.MaxHeight.ToString("0.00") + "m");
            }

            int blocked = 0, noFloor = 0, lowHeadroom = 0, ledge = 0;
            foreach (var mark in Rejected.Values)
            {
                switch (mark.Reason)
                {
                    case CargoRejectReason.Blocked: blocked++; break;
                    case CargoRejectReason.NoFloor: noFloor++; break;
                    case CargoRejectReason.LowHeadroom: lowHeadroom++; break;
                    case CargoRejectReason.Ledge: ledge++; break;
                }
            }

            CrewDebugLog.Ok(Phase,
                "Rejected columns: blocked=" + blocked
                + " noFloor=" + noFloor
                + " lowHeadroom=" + lowHeadroom
                + " ledge=" + ledge);
        }

        /// <summary>
        /// Logs what is in the column under the player: its free runs, every collider touching the column (and whether
        /// it counts as structure), and every visible mesh of the boat whose bounds reach into the column. Standing on
        /// something the painter ignores shows whether it has a collider at all or is visual geometry only.
        /// </summary>
        internal static void InspectUnderPlayer()
        {
            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.WalkCol || !context.WorldBoat)
            {
                CrewDebugLog.Warn(Phase, "Inspect: no active vessel.");
                return;
            }

            EnsureBoatColliders(context.WalkCol);
            if (!TryGetPlayerFloor(context, out Vector3 feetLocal, out float playerFloorY))
            {
                CrewDebugLog.Warn(Phase, "Inspect: " + LastProblem);
                return;
            }

            int ix = CargoArea.ToIndex(feetLocal.x);
            int iz = CargoArea.ToIndex(feetLocal.z);
            int bandLo = Mathf.FloorToInt((playerFloorY - BandBelow) / VoxelSize);
            int bandHi = Mathf.CeilToInt((playerFloorY + heightCap) / VoxelSize);
            float bandMin = bandLo * VoxelSize;
            float bandMax = bandHi * VoxelSize;

            bool previousBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                CrewDebugLog.Ok(Phase,
                    "Inspect column (" + ix + ", " + iz + ") feetLocal=" + Format(feetLocal)
                    + " playerFloorY=" + playerFloorY.ToString("0.000")
                    + " band=" + bandMin.ToString("0.00") + ".." + bandMax.ToString("0.00"));

                foreach (var run in GetColumnRuns(context.WalkCol, ix, iz, bandLo, bandHi))
                {
                    CrewDebugLog.Ok(Phase,
                        "  run floor=" + run.Run.FloorY.ToString("0.000")
                        + " ceiling=" + run.Run.CeilingY.ToString("0.000")
                        + " supported=" + run.Supported);
                }

                // Colliders: the whole band of the column, 3x3 cells wide so a beam edge beside the player shows too.
                float x = CargoArea.CellCenter(ix);
                float z = CargoArea.CellCenter(iz);
                var center = new Vector3(x, (bandMin + bandMax) * 0.5f, z);
                var halfExtents = new Vector3(VoxelSize * 1.5f, (bandMax - bandMin) * 0.5f, VoxelSize * 1.5f);
                int count = Physics.OverlapBoxNonAlloc(
                    context.WalkCol.TransformPoint(center),
                    ToWorldHalfExtents(context.WalkCol, halfExtents),
                    OverlapBuffer,
                    context.WalkCol.rotation,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Collide);

                CrewDebugLog.Ok(Phase, "  colliders touching 3x3 cells around the column: " + count);
                for (int i = 0; i < count; i++)
                {
                    var collider = OverlapBuffer[i];
                    Bounds local = LocalBounds(context.WalkCol, collider.bounds);
                    CrewDebugLog.Ok(Phase,
                        "    " + (BoatColliders.Contains(collider) ? "STRUCTURE " : "ignored   ")
                        + collider.GetType().Name
                        + (collider.isTrigger ? " trigger" : "")
                        + " layer=" + collider.gameObject.layer
                        + " localY=" + local.min.y.ToString("0.00") + ".." + local.max.y.ToString("0.00")
                        + " path='" + GetPath(collider.transform) + "'");
                }

                // Visible meshes on the real boat whose bounds reach into the same 3x3 cells.
                var probe = new Bounds(center, halfExtents * 2f);
                int meshes = 0;
                foreach (var filter in context.WorldBoat.GetComponentsInChildren<MeshFilter>())
                {
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (!filter.sharedMesh || !renderer || !renderer.enabled || filter.name == "VC_CargoAreaOverlay")
                        continue;
                    if (filter.GetComponentInParent<ShipItem>() != null)
                        continue;

                    Bounds meshBounds = MeshBoundsInBoatSpace(context.WorldBoat, filter);
                    if (!meshBounds.Intersects(probe))
                        continue;

                    meshes++;
                    CrewDebugLog.Ok(Phase,
                        "    mesh '" + filter.sharedMesh.name + "' verts=" + filter.sharedMesh.vertexCount
                        + " boatLocalY=" + meshBounds.min.y.ToString("0.00") + ".." + meshBounds.max.y.ToString("0.00")
                        + " size=" + Format(meshBounds.size)
                        + " collider=" + (filter.GetComponent<Collider>() != null)
                        + " path='" + GetPath(filter.transform) + "'");
                }

                CrewDebugLog.Ok(Phase, "  visible meshes reaching the column: " + meshes);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousBackfaces;
            }
        }

        private static Bounds LocalBounds(Transform frame, Bounds worldBounds)
        {
            var local = new Bounds(frame.InverseTransformPoint(worldBounds.center), Vector3.zero);
            Vector3 e = worldBounds.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = worldBounds.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                local.Encapsulate(frame.InverseTransformPoint(corner));
            }
            return local;
        }

        // The mesh's own bounds carried into the world boat's local frame (which matches the walk collider's).
        private static Bounds MeshBoundsInBoatSpace(Transform worldBoat, MeshFilter filter)
        {
            Bounds meshBounds = filter.sharedMesh.bounds;
            var result = new Bounds(worldBoat.InverseTransformPoint(filter.transform.TransformPoint(meshBounds.center)), Vector3.zero);
            Vector3 e = meshBounds.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = meshBounds.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(worldBoat.InverseTransformPoint(filter.transform.TransformPoint(corner)));
            }
            return result;
        }

        private static string GetPath(Transform transform)
        {
            string path = transform.name;
            for (var current = transform.parent; current != null; current = current.parent)
                path = current.name + "/" + path;
            return path;
        }

        internal static void Tick()
        {
            if (!DeveloperMode.IsEnabled)
            {
                if (Mode != CargoPaintMode.Off)
                    SetMode(CargoPaintMode.Off);
                CargoAreaOverlay.Hide();
                return;
            }

            if (Plugin.CargoPaintCycleModeKey != null
                && Plugin.CargoPaintCycleModeKey.Value.IsDown()
                && !TextInputHotkeySuppressor.ShouldSuppressFavoriteActionHotkeys)
                CycleMode();

            bool overlayWanted = ShowOverlay || Mode != CargoPaintMode.Off;
            if (!overlayWanted)
            {
                CargoAreaOverlay.Hide();
                return;
            }

            var context = CrewBoatContextResolver.Resolve();
            var area = context != null ? GetArea(context) : null;

            if (Mode != CargoPaintMode.Off && area != null && Time.time >= nextStampTime)
            {
                nextStampTime = Time.time + StampInterval;
                using (PerformanceInstrumentation.Measure("CargoAreaPainter.Stamp"))
                    Stamp(context, area);
            }

            CargoAreaOverlay.Update(context, area);
        }

        private static CargoArea GetArea(CrewBoatContext context)
        {
            if (context == null || !context.TopBoat || !context.WalkCol || !context.WorldBoat)
                return null;

            string key = context.WorldBoat.name.Replace("(Clone)", "").Trim();
            if (string.IsNullOrEmpty(key))
                return null;

            if (!Areas.TryGetValue(key, out var area))
            {
                var vessels = VirtualCrewManager.Instance.AllVesselsData;
                area = vessels != null && vessels.TryGetValue(key, out var vesselData)
                    ? CargoArea.FromSaveString(vesselData.cargoArea)
                    : new CargoArea();
                Areas[key] = area;
                if (area.RunCount > 0)
                    CrewDebugLog.Ok(Phase, "Loaded cargo area vessel='" + key + "' runs=" + area.RunCount);
            }

            return area;
        }

        /// <summary>Writes every area touched this session into the per-vessel save data, ready to be saved.</summary>
        internal static void WriteToSaveData(Dictionary<string, VesselSaveData> vessels)
        {
            if (vessels == null)
                return;

            foreach (var pair in Areas)
            {
                if (!vessels.TryGetValue(pair.Key, out var vesselData))
                {
                    if (pair.Value.RunCount == 0)
                        continue;
                    vesselData = new VesselSaveData();
                    vessels[pair.Key] = vesselData;
                }

                vesselData.cargoArea = pair.Value.ToSaveString();
            }
        }

        /// <summary>A save was loaded: drop areas held in memory so they are read afresh from the loaded data.</summary>
        internal static void OnGameLoaded()
        {
            Areas.Clear();
            Rejected.Clear();
            RejectedVersion++;
            collidersWalkCol = null;
        }

        private static void Stamp(CrewBoatContext context, CargoArea area)
        {
            EnsureBoatColliders(context.WalkCol);
            if (!TryGetPlayerFloor(context, out Vector3 feetLocal, out float playerFloorY))
                return;

            var stopwatch = Stopwatch.StartNew();
            stampQueries = 0;
            bool previousBackfaces = Physics.queriesHitBackfaces;
            // Deck meshes may be single-sided; probes from below must still see the deck above.
            Physics.queriesHitBackfaces = true;
            try
            {
                int bandLo = Mathf.FloorToInt((playerFloorY - BandBelow) / VoxelSize);
                int bandHi = Mathf.CeilToInt((playerFloorY + heightCap) / VoxelSize);

                if (Mode == CargoPaintMode.Paint)
                    PaintStamp(context.WalkCol, area, feetLocal, playerFloorY, bandLo, bandHi);
                else if (Mode == CargoPaintMode.Erase)
                    EraseStamp(area, feetLocal, bandLo, bandHi);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousBackfaces;
            }

            LastStampQueries = stampQueries;
            LastStampMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        private static void PaintStamp(Transform walkCol, CargoArea area, Vector3 feetLocal, float playerFloorY, int bandLo, int bandHi)
        {
            Visited.Clear();
            Frontier.Clear();
            LastStampRejected = 0;

            int startX = CargoArea.ToIndex(feetLocal.x);
            int startZ = CargoArea.ToIndex(feetLocal.z);
            if (!TryFindStartRun(walkCol, startX, startZ, playerFloorY, bandLo, bandHi, out var start))
            {
                LastProblem = "No paintable space under the player (too low, or obstructed).";
                return;
            }

            LastProblem = null;
            Accept(area, startX, startZ, start);
            float radiusSquared = brushRadius * brushRadius;

            while (Frontier.Count > 0)
            {
                var current = Frontier.Dequeue();
                for (int n = 0; n < 4; n++)
                {
                    int nx = current.Ix + DirX(n);
                    int nz = current.Iz + DirZ(n);

                    float dx = CargoArea.CellCenter(nx) - feetLocal.x;
                    float dz = CargoArea.CellCenter(nz) - feetLocal.z;
                    if (dx * dx + dz * dz > radiusSquared)
                        continue;

                    ExpandInto(walkCol, area, current, n, nx, nz, bandLo, bandHi);
                }
            }
        }

        // Accept every run in the neighbouring column that connects to the current run; if none does, mark why.
        private static void ExpandInto(Transform walkCol, CargoArea area, FrontierRun current, int dir, int nx, int nz, int bandLo, int bandHi)
        {
            var runs = GetColumnRuns(walkCol, nx, nz, bandLo, bandHi);
            bool connected = false;
            var reason = CargoRejectReason.Blocked;

            foreach (var candidate in runs)
            {
                var run = candidate.Run;
                float overlap = Mathf.Min(run.CeilingY, current.Run.CeilingY) - Mathf.Max(run.FloorY, current.Run.FloorY);
                if (overlap < CargoArea.MinConnectOverlap)
                    continue;

                CargoRejectReason failure;
                if (!candidate.Supported)
                    failure = CargoRejectReason.NoFloor;
                else if (run.Height < MinHeadroom)
                    failure = CargoRejectReason.LowHeadroom;
                else if (Mathf.Abs(run.FloorY - current.Run.FloorY) > CargoArea.MaxFloorStep)
                    failure = CargoRejectReason.Ledge;
                else
                {
                    connected = true;
                    if (Visited.Add(RunKey(nx, nz, candidate.Lo)))
                        Accept(area, nx, nz, run);
                    continue;
                }

                if (reason == CargoRejectReason.Blocked)
                    reason = failure;
            }

            if (connected || area.HasRunOverlapping(nx, nz, current.Run.FloorY, current.Run.CeilingY))
                return;

            LastStampRejected++;
            long key = BoundaryKey(current.Ix, current.Iz, dir);
            if (Rejected.TryGetValue(key, out var existing) && existing.Reason == reason)
                return;

            Rejected[key] = new RejectedMark { Ix = current.Ix, Iz = current.Iz, Dir = dir, Y = current.Run.FloorY, Reason = reason };
            RejectedVersion++;
        }

        private static void Accept(CargoArea area, int ix, int iz, CargoArea.Run run)
        {
            area.AddRun(ix, iz, run);
            // The boundaries into this column are no longer closed.
            for (int dir = 0; dir < 4; dir++)
                RemoveMark(ix + DirX(dir), iz + DirZ(dir), Opposite(dir));
            Frontier.Enqueue(new FrontierRun { Ix = ix, Iz = iz, Run = run });
        }

        // Removes the marks on every boundary of a column, from either side.
        private static void RemoveMarksAround(int ix, int iz)
        {
            for (int dir = 0; dir < 4; dir++)
            {
                RemoveMark(ix, iz, dir);
                RemoveMark(ix + DirX(dir), iz + DirZ(dir), Opposite(dir));
            }
        }

        private static void RemoveMark(int ix, int iz, int dir)
        {
            if (Rejected.Remove(BoundaryKey(ix, iz, dir)))
                RejectedVersion++;
        }

        private static int DirX(int dir)
        {
            return dir == 0 ? 1 : dir == 1 ? -1 : 0;
        }

        private static int DirZ(int dir)
        {
            return dir == 2 ? 1 : dir == 3 ? -1 : 0;
        }

        private static int Opposite(int dir)
        {
            return dir ^ 1;
        }

        private static bool TryFindStartRun(Transform walkCol, int ix, int iz, float playerFloorY, int bandLo, int bandHi, out CargoArea.Run start)
        {
            start = default(CargoArea.Run);
            float bestDelta = float.MaxValue;
            int bestLo = 0;

            foreach (var candidate in GetColumnRuns(walkCol, ix, iz, bandLo, bandHi))
            {
                var run = candidate.Run;
                if (!candidate.Supported || run.Height < MinHeadroom)
                    continue;
                if (run.FloorY > playerFloorY + StartStepAllowance || run.CeilingY < playerFloorY + MinHeadroom)
                    continue;

                float delta = Mathf.Abs(run.FloorY - playerFloorY);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    start = run;
                    bestLo = candidate.Lo;
                }
            }

            if (bestDelta == float.MaxValue)
                return false;

            Visited.Add(RunKey(ix, iz, bestLo));
            return true;
        }

        private static void EraseStamp(CargoArea area, Vector3 feetLocal, int bandLo, int bandHi)
        {
            int minX = CargoArea.ToIndex(feetLocal.x - brushRadius);
            int maxX = CargoArea.ToIndex(feetLocal.x + brushRadius);
            int minZ = CargoArea.ToIndex(feetLocal.z - brushRadius);
            int maxZ = CargoArea.ToIndex(feetLocal.z + brushRadius);
            float radiusSquared = brushRadius * brushRadius;
            float minY = bandLo * VoxelSize;
            float maxY = bandHi * VoxelSize;

            for (int ix = minX; ix <= maxX; ix++)
            {
                for (int iz = minZ; iz <= maxZ; iz++)
                {
                    float dx = CargoArea.CellCenter(ix) - feetLocal.x;
                    float dz = CargoArea.CellCenter(iz) - feetLocal.z;
                    if (dx * dx + dz * dz > radiusSquared)
                        continue;

                    area.EraseRuns(ix, iz, minY, maxY);
                    RemoveMarksAround(ix, iz);
                }
            }

            LastProblem = null;
            LastStampRejected = 0;
        }

        /// <summary>
        /// The free runs of a column within the band [bandLo, bandHi) (voxel indices). A run is supported when the
        /// voxel below it is structure; its floor and ceiling are then refined to the actual structure surfaces.
        /// </summary>
        private static List<ColumnRun> GetColumnRuns(Transform walkCol, int ix, int iz, int bandLo, int bandHi)
        {
            long key = CargoArea.Key(ix, iz);
            if (!Columns.TryGetValue(key, out var column))
            {
                column = new ColumnCache { BaseIy = bandLo - ColumnCacheMargin };
                Columns[key] = column;
            }

            if (column.RunsBandLo == bandLo && column.RunsBandHi == bandHi)
                return column.Runs;

            if (bandLo < column.BaseIy || bandHi > column.BaseIy + ColumnCacheVoxels)
            {
                column.BaseIy = bandLo - ColumnCacheMargin;
                column.Known = 0;
                column.Blocked = 0;
            }

            float x = CargoArea.CellCenter(ix);
            float z = CargoArea.CellCenter(iz);
            ClassifyUnknownVoxels(walkCol, x, z, column, bandLo, bandHi);

            column.Runs.Clear();
            int iy = bandLo;
            while (iy < bandHi)
            {
                if (IsBlocked(column, iy))
                {
                    iy++;
                    continue;
                }

                int lo = iy;
                while (iy < bandHi && !IsBlocked(column, iy))
                    iy++;
                int hi = iy;

                bool supported = lo > bandLo;
                bool covered = hi < bandHi;
                float floorY = supported ? RefineSurface(walkCol, x, z, lo * VoxelSize, Vector3.down) : lo * VoxelSize;
                float ceilingY = covered ? RefineSurface(walkCol, x, z, hi * VoxelSize, Vector3.up) : hi * VoxelSize;

                column.Runs.Add(new ColumnRun
                {
                    Lo = lo,
                    Hi = hi,
                    Supported = supported,
                    Run = new CargoArea.Run { FloorY = floorY, CeilingY = ceilingY }
                });
            }

            column.RunsBandLo = bandLo;
            column.RunsBandHi = bandHi;
            return column.Runs;
        }

        // Probe every not-yet-known voxel of the band, a whole unknown stretch at a time, halving only where the
        // stretch touches structure.
        private static void ClassifyUnknownVoxels(Transform walkCol, float x, float z, ColumnCache column, int bandLo, int bandHi)
        {
            int iy = bandLo;
            while (iy < bandHi)
            {
                if (IsKnown(column, iy))
                {
                    iy++;
                    continue;
                }

                int lo = iy;
                while (iy < bandHi && !IsKnown(column, iy))
                    iy++;
                Classify(walkCol, x, z, column, lo, iy);
            }
        }

        private static void Classify(Transform walkCol, float x, float z, ColumnCache column, int lo, int hi)
        {
            float bottom = lo * VoxelSize + VerticalInset;
            float top = hi * VoxelSize - VerticalInset;
            var center = new Vector3(x, (bottom + top) * 0.5f, z);
            var halfExtents = new Vector3(HorizontalHalfExtent, (top - bottom) * 0.5f, HorizontalHalfExtent);

            if (!OverlapsBoat(walkCol, center, halfExtents))
            {
                for (int iy = lo; iy < hi; iy++)
                    SetVoxel(column, iy, blocked: false);
                return;
            }

            if (hi - lo == 1)
            {
                SetVoxel(column, lo, blocked: true);
                return;
            }

            int mid = (lo + hi) / 2;
            Classify(walkCol, x, z, column, lo, mid);
            Classify(walkCol, x, z, column, mid, hi);
        }

        private static bool IsKnown(ColumnCache column, int iy)
        {
            return (column.Known & (1UL << (iy - column.BaseIy))) != 0;
        }

        private static bool IsBlocked(ColumnCache column, int iy)
        {
            return (column.Blocked & (1UL << (iy - column.BaseIy))) != 0;
        }

        private static void SetVoxel(ColumnCache column, int iy, bool blocked)
        {
            ulong bit = 1UL << (iy - column.BaseIy);
            column.Known |= bit;
            if (blocked)
                column.Blocked |= bit;
            else
                column.Blocked &= ~bit;
        }

        // Sweeps a thin slab of the column's footprint from a voxel boundary into the neighbouring blocked voxel and
        // returns the height of the structure surface it meets (the highest point under the footprint for a floor,
        // the lowest point over it for a ceiling).
        private static float RefineSurface(Transform walkCol, float x, float z, float boundaryY, Vector3 localDirection)
        {
            const float slabHalfHeight = 0.005f;
            float startY = boundaryY - localDirection.y * (VerticalInset + slabHalfHeight);
            var center = new Vector3(x, startY, z);
            var halfExtents = new Vector3(HorizontalHalfExtent, slabHalfHeight, HorizontalHalfExtent);

            if (!BoxCastBoat(walkCol, center, halfExtents, localDirection, VoxelSize + 0.01f, out float distance))
                return boundaryY;

            return startY + localDirection.y * (distance + slabHalfHeight);
        }

        private static bool TryGetPlayerFloor(CrewBoatContext context, out Vector3 feetLocal, out float floorY)
        {
            feetLocal = Vector3.zero;
            floorY = 0f;

            var controller = Refs.charController;
            if (controller == null || controller.transform.parent != context.WalkCol)
            {
                LastProblem = "The player isn't aboard the active boat.";
                return false;
            }

            Vector3 feetWorld = controller.transform.TransformPoint(controller.center + Vector3.down * (controller.height * 0.5f));
            feetLocal = context.WalkCol.InverseTransformPoint(feetWorld);

            var probe = feetLocal + Vector3.up * 0.3f;
            if (!RaycastBoat(context.WalkCol, probe, Vector3.down, 0.8f, out float distance))
            {
                LastProblem = "No boat floor found under the player.";
                return false;
            }

            floorY = probe.y - distance;
            return true;
        }

        /// <summary>True for the boat's own structural colliders (collected for <paramref name="walkCol"/>).</summary>
        internal static bool IsStructure(Transform walkCol, Collider collider)
        {
            EnsureBoatColliders(walkCol);
            return BoatColliders.Contains(collider);
        }

        private static void EnsureBoatColliders(Transform walkCol)
        {
            if (collidersWalkCol == walkCol && BoatColliders.Count > 0)
                return;

            BoatColliders.Clear();
            Columns.Clear();
            // The player's rig is parented to the walk collider while aboard; none of it is structure.
            var playerRoot = Refs.charController != null ? Refs.charController.transform : null;
            foreach (var collider in walkCol.GetComponentsInChildren<Collider>(true))
            {
                if (!collider || collider.isTrigger || collider is CharacterController)
                    continue;

                // Loose cargo is not structure either. Items on board have their physics bodies parented straight
                // to the walk collider, so check both types (as ProxyBoatBuilder does).
                if (collider.GetComponentInParent<ShipItem>() != null || collider.GetComponentInParent<ItemRigidbody>() != null)
                    continue;
                if (playerRoot && collider.transform.IsChildOf(playerRoot))
                    continue;

                BoatColliders.Add(collider);
            }

            collidersWalkCol = walkCol;
            CrewDebugLog.Ok(Phase, "Collected " + BoatColliders.Count + " structural colliders from walkCol='" + walkCol.name + "'");
        }

        // Ray in walk-collider local space; returns the local distance to the nearest structural hit.
        private static bool RaycastBoat(Transform walkCol, Vector3 localOrigin, Vector3 localDirection, float localDistance, out float hitDistance)
        {
            hitDistance = 0f;
            Vector3 worldDirection = walkCol.TransformVector(localDirection);
            float scale = worldDirection.magnitude;
            if (scale <= 0f)
                return false;

            stampQueries++;
            int count = Physics.RaycastNonAlloc(
                walkCol.TransformPoint(localOrigin),
                worldDirection / scale,
                HitBuffer,
                localDistance * scale,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            return TryGetNearestStructuralHit(count, scale, out hitDistance);
        }

        // Box sweep in walk-collider local space; returns the local distance to the nearest structural hit.
        private static bool BoxCastBoat(Transform walkCol, Vector3 localCenter, Vector3 localHalfExtents, Vector3 localDirection, float localDistance, out float hitDistance)
        {
            hitDistance = 0f;
            Vector3 worldDirection = walkCol.TransformVector(localDirection);
            float scale = worldDirection.magnitude;
            if (scale <= 0f)
                return false;

            stampQueries++;
            int count = Physics.BoxCastNonAlloc(
                walkCol.TransformPoint(localCenter),
                ToWorldHalfExtents(walkCol, localHalfExtents),
                worldDirection / scale,
                HitBuffer,
                walkCol.rotation,
                localDistance * scale,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            return TryGetNearestStructuralHit(count, scale, out hitDistance);
        }

        private static bool TryGetNearestStructuralHit(int count, float scale, out float hitDistance)
        {
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = HitBuffer[i];
                if (hit.distance < best && BoatColliders.Contains(hit.collider))
                    best = hit.distance;
            }

            hitDistance = best == float.MaxValue ? 0f : best / scale;
            return best != float.MaxValue;
        }

        private static bool OverlapsBoat(Transform walkCol, Vector3 localCenter, Vector3 localHalfExtents)
        {
            stampQueries++;
            int count = Physics.OverlapBoxNonAlloc(
                walkCol.TransformPoint(localCenter),
                ToWorldHalfExtents(walkCol, localHalfExtents),
                OverlapBuffer,
                walkCol.rotation,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
                if (BoatColliders.Contains(OverlapBuffer[i]))
                    return true;

            return false;
        }

        private static Vector3 ToWorldHalfExtents(Transform walkCol, Vector3 localHalfExtents)
        {
            Vector3 scale = walkCol.lossyScale;
            return new Vector3(
                Mathf.Abs(localHalfExtents.x * scale.x),
                Mathf.Abs(localHalfExtents.y * scale.y),
                Mathf.Abs(localHalfExtents.z * scale.z));
        }

        private static void ClearRejected()
        {
            Rejected.Clear();
            RejectedVersion++;
        }

        private static long RunKey(int ix, int iz, int lo)
        {
            return ((long)(ix & 0xFFFFF) << 40) | ((long)(iz & 0xFFFFF) << 20) | (long)(lo & 0xFFFFF);
        }

        private static long BoundaryKey(int ix, int iz, int dir)
        {
            return RunKey(ix, iz, dir);
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")";
        }
    }
}
