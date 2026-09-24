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
    ///
    /// A run's top is either real structure (a deck or beam above) or just where painting stopped looking (the painter's
    /// height limit); the latter is marked Capped. How high cargo may be stacked is the area's StackHeight, applied at
    /// solve time, so it can change without repainting: see EffectiveCeiling.
    /// </summary>
    internal sealed class CargoArea
    {
        internal const float CellSize = 0.1f;

        // Runs in neighbouring columns belong together when they overlap vertically by at least this much and their
        // floors are no further apart than the step limit.
        internal const float MinConnectOverlap = 0.2f;
        internal const float MaxFloorStep = 0.35f;

        // Tops within this of each other are the same top (saved heights are rounded to the millimetre).
        private const float SameTopTolerance = 0.002f;

        // Islands whose average floors are within this of each other are on the same level (see GetLevel).
        internal const float LevelSeparation = 0.5f;

        internal struct Run
        {
            public float FloorY;
            public float CeilingY;
            // The top is where painting stopped looking, not structure: the space may well continue above it.
            public bool Capped;

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
            public int Level;
        }

        private readonly Dictionary<long, List<Run>> columns = new Dictionary<long, List<Run>>();
        private readonly Dictionary<RunId, int> levelByRun = new Dictionary<RunId, int>();
        private int levelsVersion = -1;

        internal int RunCount { get; private set; }
        internal int ColumnCount => columns.Count;

        /// <summary>Bumped on every change, so the overlay and statistics know when to rebuild.</summary>
        internal int Version { get; private set; }

        internal float FloorAreaSquareMeters => ColumnCount * CellSize * CellSize;

        internal const float DefaultStackHeight = 2.5f;
        internal const float MinStackHeight = 0.5f;
        internal const float MaxStackHeight = 5f;
        private float stackHeight = DefaultStackHeight;

        /// <summary>How high above its floor cargo may be stacked in this area.</summary>
        internal float StackHeight
        {
            get => stackHeight;
            set
            {
                float clamped = Mathf.Clamp(value, MinStackHeight, MaxStackHeight);
                if (Mathf.Approximately(clamped, stackHeight))
                    return;
                stackHeight = clamped;
                Version++;
            }
        }

        /// <summary>
        /// The highest cargo may reach in a run: the stack height above its floor, and no higher than real structure
        /// above. A capped top is only where painting stopped, so it doesn't limit anything.
        /// </summary>
        internal float EffectiveCeiling(Run run)
        {
            float stackTop = run.FloorY + stackHeight;
            return run.Capped ? stackTop : Mathf.Min(run.CeilingY, stackTop);
        }

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

        // Save format: base64 of a version byte, the stack height (int16 millimetres), a run count, then per run the
        // column (two int16 cell indices), the floor and ceiling heights (two int16 millimetres) and a flags byte (bit 0:
        // capped). 9 bytes a run keeps a large hold to tens of KB. Version 1 had no stack height and no flags byte.
        private const byte SaveFormatVersion = 2;
        private const int RunCountOffset = 3;

        internal string ToSaveString()
        {
            if (RunCount == 0)
                return null;

            using (var stream = new System.IO.MemoryStream(RunCountOffset + 4 + RunCount * 9))
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(SaveFormatVersion);
                writer.Write((short)ToMillimetres(stackHeight));
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
                    writer.Write((byte)(painted.Run.Capped ? 1 : 0));
                    written++;
                }

                writer.Flush();
                stream.Position = RunCountOffset;
                writer.Write(written);
                return System.Convert.ToBase64String(stream.ToArray());
            }
        }

        /// <summary>
        /// Reads an area written by <see cref="ToSaveString"/>; returns an empty area for missing or unreadable data. Areas
        /// from version 1 have every top treated as real structure; repainting over them records which tops are capped.
        /// </summary>
        internal static CargoArea FromSaveString(string data)
        {
            var area = new CargoArea();
            if (string.IsNullOrEmpty(data))
                return area;

            try
            {
                using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(System.Convert.FromBase64String(data))))
                {
                    byte version = reader.ReadByte();
                    if (version != 1 && version != SaveFormatVersion)
                        return area;

                    if (version >= 2)
                        area.stackHeight = Mathf.Clamp(reader.ReadInt16() / 1000f, MinStackHeight, MaxStackHeight);

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int ix = reader.ReadInt16();
                        int iz = reader.ReadInt16();
                        float floorY = reader.ReadInt16() / 1000f;
                        float ceilingY = reader.ReadInt16() / 1000f;
                        bool capped = version >= 2 && (reader.ReadByte() & 1) != 0;
                        area.AddRun(ix, iz, new Run { FloorY = floorY, CeilingY = ceilingY, Capped = capped });
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

            for (int i = 0; i < column.Count; i++)
            {
                var existing = column[i];
                if (existing.FloorY > run.FloorY || existing.CeilingY < run.CeilingY)
                    continue;

                // Already covered. A fresh probe reaching the same top knows best whether that top is capped.
                if (Mathf.Abs(existing.CeilingY - run.CeilingY) < SameTopTolerance && existing.Capped != run.Capped)
                {
                    existing.Capped = run.Capped;
                    column[i] = existing;
                    Version++;
                }
                return;
            }

            int before = column.Count;
            for (int i = column.Count - 1; i >= 0; i--)
            {
                var existing = column[i];
                if (existing.FloorY > run.CeilingY + 0.001f || existing.CeilingY < run.FloorY - 0.001f)
                    continue;

                // The merged run's top is the higher of the two, and capped or not as that one was.
                run.FloorY = Mathf.Min(run.FloorY, existing.FloorY);
                if (existing.CeilingY > run.CeilingY + SameTopTolerance)
                {
                    run.CeilingY = existing.CeilingY;
                    run.Capped = existing.Capped;
                }
                else
                {
                    run.CeilingY = Mathf.Max(run.CeilingY, existing.CeilingY);
                }
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
            var islandColumns = new HashSet<long>();
            foreach (var members in CollectIslands())
            {
                var island = new Island
                {
                    Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                    Max = new Vector3(float.MinValue, float.MinValue, float.MinValue),
                    MinHeight = float.MaxValue,
                    MaxHeight = float.MinValue
                };
                islandColumns.Clear();
                foreach (var painted in members)
                {
                    islandColumns.Add(Key(painted.Ix, painted.Iz));
                    Accumulate(ref island, painted);
                }

                island.ColumnCount = islandColumns.Count;
                island.Level = GetLevel(members[0].Ix, members[0].Iz, members[0].Run.FloorY);
                islands.Add(island);
            }

            islands.Sort((a, b) => b.Volume.CompareTo(a.Volume));
            return islands;
        }

        /// <summary>
        /// Which level a painted run belongs to: its island's rank from the bottom, counting islands whose average
        /// floors are within <see cref="LevelSeparation"/> of each other as one level (a hold split by a bulkhead). The
        /// solver fills level 0 (the lowest, usually the hold) before putting anything on level 1, and so on.
        /// </summary>
        internal int GetLevel(int ix, int iz, float floorY)
        {
            if (levelsVersion != Version)
                ComputeLevels();
            return levelByRun.TryGetValue(new RunId(ix, iz, floorY), out int level) ? level : 0;
        }

        private void ComputeLevels()
        {
            levelByRun.Clear();
            levelsVersion = Version;

            var islands = CollectIslands();
            var meanFloors = new float[islands.Count];
            var order = new int[islands.Count];
            for (int i = 0; i < islands.Count; i++)
            {
                float sum = 0f;
                foreach (var painted in islands[i])
                    sum += painted.Run.FloorY;
                meanFloors[i] = sum / islands[i].Count;
                order[i] = i;
            }

            System.Array.Sort(order, (a, b) => meanFloors[a].CompareTo(meanFloors[b]));
            int level = 0;
            float levelFloor = islands.Count > 0 ? meanFloors[order[0]] : 0f;
            foreach (int index in order)
            {
                if (meanFloors[index] > levelFloor + LevelSeparation)
                {
                    level++;
                    levelFloor = meanFloors[index];
                }

                foreach (var painted in islands[index])
                    levelByRun[new RunId(painted)] = level;
            }
        }

        // The runs of each island: runs connected across neighbouring columns (see AreConnected).
        private List<List<PaintedRun>> CollectIslands()
        {
            var islands = new List<List<PaintedRun>>();
            var visited = new HashSet<RunId>();
            var queue = new Queue<PaintedRun>();

            foreach (var start in EnumerateRuns())
            {
                if (!visited.Add(new RunId(start)))
                    continue;

                var members = new List<PaintedRun>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    members.Add(current);

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

                islands.Add(members);
            }

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
                : this(painted.Ix, painted.Iz, painted.Run.FloorY)
            {
            }

            internal RunId(int ix, int iz, float floorY)
            {
                this.ix = ix;
                this.iz = iz;
                this.floorY = floorY;
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
