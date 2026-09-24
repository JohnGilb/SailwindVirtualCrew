using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// A painted cargo area on one vessel: the free space cargo may occupy, stored as a sparse grid of 10cm columns in
    /// boat-local space (the walk collider's local frame, which is also the world boat's local frame). Each column holds
    /// one or more free runs: vertical stretches between the structure the run rests on and the structure (or height
    /// cap) above it. Several runs per column cover overhangs, shelves and stacked decks; runs whose floors step up
    /// column by column follow a sloped hull.
    /// </summary>
    internal sealed class CargoArea
    {
        internal const float CellSize = 0.1f;

        // Runs in neighbouring columns belong together when they overlap vertically by at least this much and their
        // floors are no further apart than the step limit.
        internal const float MinConnectOverlap = 0.2f;
        internal const float MaxFloorStep = 0.35f;

        internal struct Run
        {
            public float FloorY;
            public float CeilingY;

            public float Height => CeilingY - FloorY;
        }

        internal struct PaintedRun
        {
            public int Ix;
            public int Iz;
            public Run Run;
        }

        internal struct Island
        {
            public int ColumnCount;
            public float Volume;
            public Vector3 Min;
            public Vector3 Max;
            public float MinHeight;
            public float MaxHeight;
        }

        private readonly Dictionary<long, List<Run>> columns = new Dictionary<long, List<Run>>();

        internal int RunCount { get; private set; }
        internal int ColumnCount => columns.Count;

        /// <summary>Bumped on every change, so the overlay and statistics know when to rebuild.</summary>
        internal int Version { get; private set; }

        internal float FloorAreaSquareMeters => ColumnCount * CellSize * CellSize;

        internal float VolumeCubicMeters
        {
            get
            {
                float volume = 0f;
                foreach (var column in columns.Values)
                    foreach (var run in column)
                        volume += run.Height;
                return volume * CellSize * CellSize;
            }
        }

        // Save format: base64 of a version byte, a run count, then per run the column (two int16 cell indices) and the
        // floor and ceiling heights (two int16 millimetres). 8 bytes a run keeps a large hold to tens of KB.
        private const byte SaveFormatVersion = 1;

        internal string ToSaveString()
        {
            if (RunCount == 0)
                return null;

            using (var stream = new System.IO.MemoryStream(5 + RunCount * 8))
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(SaveFormatVersion);
                writer.Write(0);
                int written = 0;
                foreach (var painted in EnumerateRuns())
                {
                    if (!FitsInt16(painted.Ix) || !FitsInt16(painted.Iz)
                        || !FitsInt16(ToMillimetres(painted.Run.FloorY)) || !FitsInt16(ToMillimetres(painted.Run.CeilingY)))
                        continue;

                    writer.Write((short)painted.Ix);
                    writer.Write((short)painted.Iz);
                    writer.Write((short)ToMillimetres(painted.Run.FloorY));
                    writer.Write((short)ToMillimetres(painted.Run.CeilingY));
                    written++;
                }

                writer.Flush();
                stream.Position = 1;
                writer.Write(written);
                return System.Convert.ToBase64String(stream.ToArray());
            }
        }

        /// <summary>Reads an area written by <see cref="ToSaveString"/>; returns an empty area for missing or unreadable data.</summary>
        internal static CargoArea FromSaveString(string data)
        {
            var area = new CargoArea();
            if (string.IsNullOrEmpty(data))
                return area;

            try
            {
                using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(System.Convert.FromBase64String(data))))
                {
                    if (reader.ReadByte() != SaveFormatVersion)
                        return area;

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int ix = reader.ReadInt16();
                        int iz = reader.ReadInt16();
                        float floorY = reader.ReadInt16() / 1000f;
                        float ceilingY = reader.ReadInt16() / 1000f;
                        area.AddRun(ix, iz, new Run { FloorY = floorY, CeilingY = ceilingY });
                    }
                }
            }
            catch (System.Exception ex)
            {
                CrewDebugLog.Warn("CargoPaint", "Could not read saved cargo area: " + ex.GetType().Name + ": " + ex.Message);
                area.Clear();
            }

            return area;
        }

        private static int ToMillimetres(float metres)
        {
            return Mathf.RoundToInt(metres * 1000f);
        }

        private static bool FitsInt16(int value)
        {
            return value >= short.MinValue && value <= short.MaxValue;
        }

        internal static int ToIndex(float coordinate)
        {
            return Mathf.FloorToInt(coordinate / CellSize);
        }

        internal static float CellCenter(int index)
        {
            return (index + 0.5f) * CellSize;
        }

        internal static bool AreConnected(Run a, Run b)
        {
            float overlap = Mathf.Min(a.CeilingY, b.CeilingY) - Mathf.Max(a.FloorY, b.FloorY);
            return overlap >= MinConnectOverlap && Mathf.Abs(a.FloorY - b.FloorY) <= MaxFloorStep;
        }

        /// <summary>The runs painted in one column (read-only; do not modify the list).</summary>
        internal bool TryGetColumn(int ix, int iz, out List<Run> runs)
        {
            return columns.TryGetValue(Key(ix, iz), out runs);
        }

        internal bool HasRunOverlapping(int ix, int iz, float minY, float maxY)
        {
            if (!columns.TryGetValue(Key(ix, iz), out var column))
                return false;

            foreach (var run in column)
                if (run.FloorY < maxY && run.CeilingY > minY)
                    return true;
            return false;
        }

        /// <summary>Adds a run, merging it with any stored run in that column it overlaps or touches.</summary>
        internal void AddRun(int ix, int iz, Run run)
        {
            long key = Key(ix, iz);
            if (!columns.TryGetValue(key, out var column))
            {
                column = new List<Run>(1);
                columns[key] = column;
            }

            foreach (var existing in column)
                if (existing.FloorY <= run.FloorY && existing.CeilingY >= run.CeilingY)
                    return;

            int before = column.Count;
            for (int i = column.Count - 1; i >= 0; i--)
            {
                var existing = column[i];
                if (existing.FloorY > run.CeilingY + 0.001f || existing.CeilingY < run.FloorY - 0.001f)
                    continue;

                run.FloorY = Mathf.Min(run.FloorY, existing.FloorY);
                run.CeilingY = Mathf.Max(run.CeilingY, existing.CeilingY);
                column.RemoveAt(i);
            }

            column.Add(run);
            RunCount += column.Count - before;
            Version++;
        }

        /// <summary>Removes the runs in a column that overlap a height band.</summary>
        internal int EraseRuns(int ix, int iz, float minY, float maxY)
        {
            long key = Key(ix, iz);
            if (!columns.TryGetValue(key, out var column))
                return 0;

            int removed = column.RemoveAll(r => r.FloorY < maxY && r.CeilingY > minY);
            if (column.Count == 0)
                columns.Remove(key);

            if (removed > 0)
            {
                RunCount -= removed;
                Version++;
            }

            return removed;
        }

        internal void Clear()
        {
            if (columns.Count == 0)
                return;

            columns.Clear();
            RunCount = 0;
            Version++;
        }

        internal IEnumerable<PaintedRun> EnumerateRuns()
        {
            foreach (var pair in columns)
            {
                Unkey(pair.Key, out int ix, out int iz);
                foreach (var run in pair.Value)
                    yield return new PaintedRun { Ix = ix, Iz = iz, Run = run };
            }
        }

        /// <summary>Splits the painted runs into islands of runs connected across neighbouring columns.</summary>
        internal List<Island> FindIslands()
        {
            var islands = new List<Island>();
            var visited = new HashSet<RunId>();
            var queue = new Queue<PaintedRun>();
            var islandColumns = new HashSet<long>();

            foreach (var start in EnumerateRuns())
            {
                if (!visited.Add(new RunId(start)))
                    continue;

                var island = new Island
                {
                    Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                    Max = new Vector3(float.MinValue, float.MinValue, float.MinValue),
                    MinHeight = float.MaxValue,
                    MaxHeight = float.MinValue
                };
                islandColumns.Clear();

                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    islandColumns.Add(Key(current.Ix, current.Iz));
                    Accumulate(ref island, current);

                    for (int n = 0; n < 4; n++)
                    {
                        int nx = current.Ix + (n == 0 ? 1 : n == 1 ? -1 : 0);
                        int nz = current.Iz + (n == 2 ? 1 : n == 3 ? -1 : 0);
                        if (!columns.TryGetValue(Key(nx, nz), out var column))
                            continue;

                        foreach (var run in column)
                        {
                            if (!AreConnected(current.Run, run))
                                continue;

                            var neighbour = new PaintedRun { Ix = nx, Iz = nz, Run = run };
                            if (visited.Add(new RunId(neighbour)))
                                queue.Enqueue(neighbour);
                        }
                    }
                }

                island.ColumnCount = islandColumns.Count;
                islands.Add(island);
            }

            islands.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            return islands;
        }

        private static void Accumulate(ref Island island, PaintedRun painted)
        {
            float x0 = painted.Ix * CellSize;
            float z0 = painted.Iz * CellSize;
            island.Volume += painted.Run.Height * CellSize * CellSize;
            island.Min = Vector3.Min(island.Min, new Vector3(x0, painted.Run.FloorY, z0));
            island.Max = Vector3.Max(island.Max, new Vector3(x0 + CellSize, painted.Run.CeilingY, z0 + CellSize));
            island.MinHeight = Mathf.Min(island.MinHeight, painted.Run.Height);
            island.MaxHeight = Mathf.Max(island.MaxHeight, painted.Run.Height);
        }

        internal static long Key(int ix, int iz)
        {
            return ((long)ix << 32) | (uint)iz;
        }

        internal static void Unkey(long key, out int ix, out int iz)
        {
            ix = (int)(key >> 32);
            iz = (int)(key & 0xFFFFFFFFL);
        }

        // Identifies one stored run: its column plus its exact stored floor height.
        private struct RunId : System.IEquatable<RunId>
        {
            private readonly int ix;
            private readonly int iz;
            private readonly float floorY;

            internal RunId(PaintedRun painted)
            {
                ix = painted.Ix;
                iz = painted.Iz;
                floorY = painted.Run.FloorY;
            }

            public bool Equals(RunId other)
            {
                return ix == other.ix && iz == other.iz && floorY == other.floorY;
            }

            public override bool Equals(object obj)
            {
                return obj is RunId other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ix * 73856093) ^ (iz * 19349663) ^ floorY.GetHashCode();
                }
            }
        }
    }
}
