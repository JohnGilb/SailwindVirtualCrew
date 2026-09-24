using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Developer spike: find where one cargo item should go in the painted cargo area, and show it as a ghost box.
    ///
    /// Candidates are the item's collision box, upright, turned square to the boat (0 or 90 degrees), with its
    /// footprint starting at every painted cell. A coarse pass keeps candidates whose footprint lies wholly inside
    /// painted free space with room under the ceiling, lowest first. Each survivor is then tested for real in the walk
    /// collider, against the boat's structure and the cargo already aboard: the box is swept down from as high as it
    /// fits to find where it would come to rest, the surface under it is sampled to judge whether it would stay put,
    /// and its sides are probed for walls and neighbouring cargo to pack against. The lowest stable spot wins, ties going
    /// to the one touching the most sides.
    ///
    /// Look at an item (or hold it) and press the solve key to preview; press it again on the same item to move the
    /// item there, after which its drift while settling is measured.
    /// </summary>
    internal static partial class CargoPackingSolver
    {
        private const string Phase = "CargoSolve";

        private const float VoxelSize = CargoArea.CellSize;
        // The swept box is this much smaller than the item on every side, so resting contact doesn't count as overlap.
        private const float Skin = 0.005f;
        // Rest poses in the same band of height count as equally low.
        private const float LevelBucket = 0.05f;
        // Physics testing stops after this long, or this many candidates, whichever comes first.
        private const float PhysicsBudgetMilliseconds = 40f;
        private const int MaxPhysicsCandidates = 4000;
        // Cargo bounds are shrunk by this much so neighbouring columns a crate only touches aren't counted as covered.
        private const float CargoBoundsInset = 0.02f;
        private const int SupportSamplesPerSide = 4;
        private const float SupportProbeDepth = 0.04f;
        private const float MinFlatNormalY = 0.85f;
        private const float ContactGap = 0.03f;
        private const float PlaceLift = 0.01f;
        private const float SettleSeconds = 3f;

        private static readonly float[] Yaws = { 0f, 90f };

        internal enum Outcome
        {
            // The box can't start above the spot: structure or cargo is already there.
            NoRoom,
            // The drop found nothing to land on above the painted floor.
            NoSurface,
            // The drop landed on something, but none of the probes under the box reach it: it is caught on an edge or
            // rim (another barrel's lip, a ledge) that it would slide off.
            Hanging,
            // Supported, but not around its centre, or mostly on a slope.
            Unstable,
            Valid
        }

        internal struct Placement
        {
            public Vector3 CenterLocal;
            public Quaternion RotationLocal;
            public Vector3 Size;
            public float RestBottomY;
            public int SupportHits;
            public int FlatHits;
            public int SideContacts;
        }

        private struct Candidate
        {
            public int Ix;
            public int Iz;
            public int YawIndex;
            // Highest painted floor under the footprint: the lowest the item can possibly rest (cargo only raises it).
            public float FloorRest;
            // Where it would probably rest counting cargo already aboard; only orders the candidates.
            public float EstimatedRest;
            public float MinCeiling;
            // The lowest painted floor under the footprint: the drop test searches down to here, since cargo bounds
            // used for EstimatedRest can overstate what is actually underneath.
            public float MinFloor;
        }

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[64];
        private static readonly Collider[] OverlapBuffer = new Collider[64];
        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly HashSet<long> SeenCandidates = new HashSet<long>();
        private static readonly int[] OutcomeCounts = new int[5];
        // A few examples of each failed outcome from the last solve, for the log.
        private static readonly List<string>[] OutcomeSamples = { new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>() };
        private const int SamplesPerOutcome = 3;
        private static string lastSweepHit;
        private static string lastCoarseFailure;
        // Height intervals occupied by cargo already aboard, per column, from each item's boat-local bounds.
        private static readonly Dictionary<long, List<Vector2>> CargoColumns = new Dictionary<long, List<Vector2>>();

        private static Transform walkCol;
        private static ItemRigidbody selectedBody;
        private static Vector3 itemBoxCenter;
        private static GameObject ghost;
        private static Material ghostMaterial;

        private static ShipItem settleItem;
        private static float settleTime;
        private static Vector3 settleExpectedPosition;
        private static Quaternion settleExpectedRotation;

        internal static ShipItem SelectedItem { get; private set; }
        internal static bool HasPlacement { get; private set; }
        internal static Placement Best { get; private set; }
        internal static int CandidateCount { get; private set; }
        internal static int PhysicsEvaluated { get; private set; }
        internal static bool BudgetExhausted { get; private set; }
        internal static float LastSolveMilliseconds { get; private set; }
        internal static string LastMessage { get; private set; }

        internal static int CountOf(Outcome outcome)
        {
            return OutcomeCounts[(int)outcome];
        }

        /// <summary>The solve key was pressed while looking at or holding <paramref name="item"/>.</summary>
        internal static void OnSolveKey(ShipItem item, bool held)
        {
            if (!item)
                return;

            if (!held && item == SelectedItem && HasPlacement)
            {
                PlaceSelected();
                return;
            }

            Solve(item);
            if (held && HasPlacement)
                Notify("Preview shown. Drop the item and press again while looking at it to place it.");
        }

        internal static void Solve(ShipItem item)
        {
            ClearPreview();
            SelectedItem = item;

            var context = CrewBoatContextResolver.Resolve();
            var area = CargoAreaPainter.GetActiveArea();
            if (context == null || area == null || area.RunCount == 0)
            {
                Notify("Paint a cargo area on this boat first.");
                return;
            }

            if (!TryGetItemBox(item, out itemBoxCenter, out Vector3 size))
            {
                Notify("Can't work out the shape of '" + item.name + "'.");
                return;
            }

            walkCol = context.WalkCol;
            selectedBody = item.GetItemRigidbody();

            var stopwatch = Stopwatch.StartNew();
            bool previousBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                BuildCargoColumns();
                BuildCandidates(area, size);
                EvaluateCandidates(size);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousBackfaces;
            }

            LastSolveMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            string counts = "candidates=" + CandidateCount
                + " physics=" + PhysicsEvaluated
                + " noRoom=" + CountOf(Outcome.NoRoom)
                + " noSurface=" + CountOf(Outcome.NoSurface)
                + " hanging=" + CountOf(Outcome.Hanging)
                + " unstable=" + CountOf(Outcome.Unstable)
                + " valid=" + CountOf(Outcome.Valid)
                + " ms=" + LastSolveMilliseconds.ToString("0.0")
                + (BudgetExhausted ? " BUDGET-EXHAUSTED" : "")
                + " cargoColumns=" + CargoColumns.Count
                + " itemColliders=" + DescribeColliders(item);

            if (!HasPlacement)
            {
                Notify("No stable spot for '" + item.name + "' in the painted area.");
                CrewDebugLog.Ok(Phase, "Solve item='" + item.name + "' size=" + Format(size) + " FAILED " + counts);
                LogOutcomeSamples();
                return;
            }

            var best = Best;
            ShowGhost(context.WorldBoat, best);
            LastMessage = "Best spot for '" + item.name + "': rest " + best.RestBottomY.ToString("0.00")
                + "m, support " + best.SupportHits + "/" + (SupportSamplesPerSide * SupportSamplesPerSide)
                + ", " + best.SideContacts + " side" + (best.SideContacts == 1 ? "" : "s") + " touching.";
            CrewDebugLog.Ok(Phase,
                "Solve item='" + item.name + "' size=" + Format(size)
                + " best center=" + Format(best.CenterLocal)
                + " yaw=" + best.RotationLocal.eulerAngles.y.ToString("0")
                + " restY=" + best.RestBottomY.ToString("0.000")
                + " support=" + best.SupportHits + " flat=" + best.FlatHits
                + " contacts=" + best.SideContacts
                + " " + counts);
            LogOutcomeSamples();
        }

        private static void LogOutcomeSamples()
        {
            for (int i = 0; i < OutcomeSamples.Length; i++)
                foreach (var sample in OutcomeSamples[i])
                    CrewDebugLog.Ok(Phase, "  " + (Outcome)i + ": " + sample);
        }

        /// <summary>Moves the selected item (which must be aboard and not held) to the previewed spot.</summary>
        internal static bool PlaceSelected()
        {
            var item = SelectedItem;
            if (!item || !HasPlacement)
                return false;

            var context = CrewBoatContextResolver.Resolve();
            if (context == null || item.held)
            {
                Notify("Drop the item before placing it.");
                return false;
            }

            if (item.currentActualBoat != context.WorldBoat || item.currentWalkCol != context.WalkCol)
            {
                Notify("The spike can only move items already aboard this boat.");
                return false;
            }

            var body = item.GetItemRigidbody();
            var rigidbody = body ? body.GetBody() : null;
            if (!body || !rigidbody)
            {
                Notify("'" + item.name + "' has no physics body yet.");
                return false;
            }

            var best = Best;
            Quaternion rotation = best.RotationLocal;
            // The box centre is the item's collision box centre; the item's own origin is offset from it.
            Vector3 position = best.CenterLocal - rotation * itemBoxCenter + Vector3.up * PlaceLift;

            item.transform.position = context.WorldBoat.TransformPoint(position);
            item.transform.rotation = context.WorldBoat.rotation * rotation;

            Vector3 bodyPosition = context.WalkCol.TransformPoint(position);
            Quaternion bodyRotation = context.WalkCol.rotation * rotation;
            body.transform.position = bodyPosition;
            body.transform.rotation = bodyRotation;
            rigidbody.position = bodyPosition;
            rigidbody.rotation = bodyRotation;
            rigidbody.velocity = Vector3.zero;
            rigidbody.angularVelocity = Vector3.zero;

            settleItem = item;
            settleTime = Time.time + SettleSeconds;
            settleExpectedPosition = position;
            settleExpectedRotation = rotation;

            CrewDebugLog.Ok(Phase, "Placed item='" + item.name + "' at local=" + Format(position) + " yaw=" + rotation.eulerAngles.y.ToString("0"));
            ClearPreview();
            SelectedItem = null;
            Notify("Placed. Measuring how it settles...");
            return true;
        }

        internal static void ClearPreview()
        {
            HasPlacement = false;
            if (ghost)
                Object.Destroy(ghost);
            ghost = null;
        }

        internal static void Tick()
        {
            if (!DeveloperMode.IsEnabled)
            {
                if (SelectedItem || ghost)
                {
                    ClearPreview();
                    SelectedItem = null;
                }
                return;
            }

            if (ghost && !SelectedItem)
                ClearPreview();

            if (settleItem && Time.time >= settleTime)
                ReportSettle();
        }

        private static void ReportSettle()
        {
            var item = settleItem;
            settleItem = null;
            var worldBoat = item.currentActualBoat;
            if (!worldBoat)
            {
                Notify("'" + item.name + "' left the boat while settling!");
                CrewDebugLog.Warn(Phase, "Settle item='" + item.name + "' left the boat.");
                return;
            }

            Vector3 position = worldBoat.InverseTransformPoint(item.transform.position);
            Quaternion rotation = Quaternion.Inverse(worldBoat.rotation) * item.transform.rotation;
            float drift = Vector3.Distance(position, settleExpectedPosition);
            float turned = Quaternion.Angle(rotation, settleExpectedRotation);

            string message = "Settled: moved " + (drift * 100f).ToString("0") + "cm, turned " + turned.ToString("0") + "°";
            Notify(message);
            CrewDebugLog.Ok(Phase,
                "Settle item='" + item.name + "' drift=" + drift.ToString("0.000")
                + "m angle=" + turned.ToString("0.0") + "deg expected=" + Format(settleExpectedPosition)
                + " actual=" + Format(position));
        }

        // Candidate generation: every painted cell as the footprint's minimum corner, for each yaw. The footprint's
        // columns must each have a painted run overlapping the item's height above the anchor run; the box would rest
        // on the highest of their floors and must fit under the lowest of their ceilings.
        private static void BuildCandidates(CargoArea area, Vector3 size)
        {
            Candidates.Clear();
            SeenCandidates.Clear();

            for (int yawIndex = 0; yawIndex < Yaws.Length; yawIndex++)
            {
                float sizeX = yawIndex == 0 ? size.x : size.z;
                float sizeZ = yawIndex == 0 ? size.z : size.x;
                int cellsX = Mathf.Max(1, Mathf.CeilToInt((sizeX - 0.001f) / VoxelSize));
                int cellsZ = Mathf.Max(1, Mathf.CeilToInt((sizeZ - 0.001f) / VoxelSize));

                foreach (var anchor in area.EnumerateRuns())
                {
                    if (!TryCoarseFit(area, anchor, cellsX, cellsZ, size.y, out float floorRest, out float estimatedRest, out float minCeiling, out float minFloor))
                        continue;

                    long key = ((long)(anchor.Ix & 0xFFFFF) << 40) | ((long)(anchor.Iz & 0xFFFFF) << 20)
                        | ((long)yawIndex << 16) | (long)(Mathf.RoundToInt(floorRest / LevelBucket) & 0xFFFF);
                    if (!SeenCandidates.Add(key))
                        continue;

                    Candidates.Add(new Candidate
                    {
                        Ix = anchor.Ix,
                        Iz = anchor.Iz,
                        YawIndex = yawIndex,
                        FloorRest = floorRest,
                        EstimatedRest = estimatedRest,
                        MinCeiling = minCeiling,
                        MinFloor = minFloor
                    });
                }
            }

            Candidates.Sort((a, b) => a.EstimatedRest.CompareTo(b.EstimatedRest));
            CandidateCount = Candidates.Count;
        }

        private static bool TryCoarseFit(CargoArea area, CargoArea.PaintedRun anchor, int cellsX, int cellsZ, float height, out float floorRest, out float estimatedRest, out float minCeiling, out float minFloor)
        {
            floorRest = float.MinValue;
            estimatedRest = float.MinValue;
            minCeiling = float.MaxValue;
            minFloor = float.MaxValue;
            float bandMin = anchor.Run.FloorY;
            float bandMax = anchor.Run.FloorY + height;

            for (int dx = 0; dx < cellsX; dx++)
            {
                for (int dz = 0; dz < cellsZ; dz++)
                {
                    if (!area.TryGetColumn(anchor.Ix + dx, anchor.Iz + dz, out var runs))
                    {
                        lastCoarseFailure = "column (" + (anchor.Ix + dx) + ", " + (anchor.Iz + dz) + ") not painted";
                        return false;
                    }

                    // The run in this column that best overlaps the item's height above the anchor.
                    bool found = false;
                    float bestOverlap = 0f;
                    var chosen = default(CargoArea.Run);
                    foreach (var run in runs)
                    {
                        float overlap = Mathf.Min(run.CeilingY, bandMax) - Mathf.Max(run.FloorY, bandMin);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            chosen = run;
                            found = true;
                        }
                    }

                    if (!found)
                    {
                        lastCoarseFailure = "column (" + (anchor.Ix + dx) + ", " + (anchor.Iz + dz) + ") painted only at other heights";
                        return false;
                    }

                    // The painted area ignores cargo; the item would rest on top of any cargo already in this run.
                    float floor = chosen.FloorY;
                    if (CargoColumns.TryGetValue(CargoArea.Key(anchor.Ix + dx, anchor.Iz + dz), out var cargo))
                        foreach (var interval in cargo)
                            if (interval.y > floor && interval.x < chosen.CeilingY)
                                floor = interval.y;

                    floorRest = Mathf.Max(floorRest, chosen.FloorY);
                    estimatedRest = Mathf.Max(estimatedRest, floor);
                    minCeiling = Mathf.Min(minCeiling, chosen.CeilingY);
                    minFloor = Mathf.Min(minFloor, chosen.FloorY);
                }
            }

            // Feasibility is judged on the painted space alone. Cargo bounds are only an estimate (a barrel's square
            // bounds claim floor its round body leaves free), so cargo reorders candidates and physics decides. When the
            // estimate says the item would have to go on top of cargo that leaves no headroom, try it last.
            if (floorRest + height > minCeiling + 0.01f)
            {
                lastCoarseFailure = "no headroom: floor " + floorRest.ToString("0.00") + " + height " + height.ToString("0.00")
                    + " > ceiling " + minCeiling.ToString("0.00");
                return false;
            }
            if (estimatedRest + height > minCeiling + 0.01f)
                estimatedRest = floorRest + 100f;
            return true;
        }

        // Records the boat-local height interval of every cargo item aboard (except the one being solved) in each column
        // its bounds cover, so the coarse pass can stack on cargo instead of sending occupied spots to physics.
        private static void BuildCargoColumns()
        {
            CargoColumns.Clear();
            foreach (var body in walkCol.GetComponentsInChildren<ItemRigidbody>())
            {
                if (body == selectedBody)
                    continue;

                // Held items' colliders are triggers; they aren't sitting anywhere.
                bool held = false;
                foreach (var collider in body.GetComponentsInChildren<Collider>())
                    held |= collider.isTrigger;
                if (held || !TryGetBodyBox(body, out Vector3 boxCenter, out Vector3 boxSize))
                    continue;

                // The item's box in walk-collider space: centre, rotation, and its world-aligned bounds.
                Vector3 center = walkCol.InverseTransformPoint(body.transform.TransformPoint(boxCenter));
                Quaternion rotation = Quaternion.Inverse(walkCol.rotation) * body.transform.rotation;
                Quaternion inverse = Quaternion.Inverse(rotation);
                Vector3 half = boxSize * 0.5f;
                var bounds = new Bounds(center, Vector3.zero);
                for (int i = 0; i < 8; i++)
                    bounds.Encapsulate(center + rotation * new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z));

                var interval = new Vector2(bounds.min.y, bounds.max.y);
                int x0 = CargoArea.ToIndex(bounds.min.x);
                int x1 = CargoArea.ToIndex(bounds.max.x);
                int z0 = CargoArea.ToIndex(bounds.min.z);
                int z1 = CargoArea.ToIndex(bounds.max.z);
                for (int ix = x0; ix <= x1; ix++)
                {
                    for (int iz = z0; iz <= z1; iz++)
                    {
                        // Only columns whose centre lies inside the item's own (turned) footprint.
                        Vector3 inBox = inverse * (new Vector3(CargoArea.CellCenter(ix), center.y, CargoArea.CellCenter(iz)) - center);
                        if (Mathf.Abs(inBox.x) > half.x - CargoBoundsInset || Mathf.Abs(inBox.z) > half.z - CargoBoundsInset)
                            continue;

                        long key = CargoArea.Key(ix, iz);
                        if (!CargoColumns.TryGetValue(key, out var list))
                        {
                            list = new List<Vector2>(1);
                            CargoColumns[key] = list;
                        }
                        list.Add(interval);
                    }
                }
            }
        }

        private static void EvaluateCandidates(Vector3 size)
        {
            HasPlacement = false;
            PhysicsEvaluated = 0;
            System.Array.Clear(OutcomeCounts, 0, OutcomeCounts.Length);
            foreach (var samples in OutcomeSamples)
                samples.Clear();
            float bestScore = float.MaxValue;
            var best = default(Placement);
            var stopwatch = Stopwatch.StartNew();
            BudgetExhausted = false;

            foreach (var candidate in Candidates)
            {
                // Candidates are sorted by their coarse rest height; once they are clearly higher than the best stable
                // spot found, nothing later can beat it.
                // Nothing can rest below its painted floor, so a candidate whose floor is clearly higher than the best
                // stable spot found can't beat it.
                if (HasPlacement && candidate.FloorRest > best.RestBottomY + LevelBucket + 0.05f)
                    continue;
                if (PhysicsEvaluated >= MaxPhysicsCandidates || stopwatch.Elapsed.TotalMilliseconds > PhysicsBudgetMilliseconds)
                {
                    BudgetExhausted = true;
                    break;
                }

                PhysicsEvaluated++;
                lastSweepHit = null;
                var outcome = Evaluate(candidate, size, out var placement, out float restBottom, out int supportHits);
                OutcomeCounts[(int)outcome]++;
                if (outcome != Outcome.Valid)
                {
                    RecordSample(outcome, candidate, size, restBottom, supportHits);
                    continue;
                }

                float score = Mathf.Floor(placement.RestBottomY / LevelBucket) * 1000f
                    - placement.SideContacts * 10f
                    - placement.SupportHits;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                    HasPlacement = true;
                }
            }

            Best = best;
        }

        private static void RecordSample(Outcome outcome, Candidate candidate, Vector3 size, float restBottom, int supportHits)
        {
            var samples = OutcomeSamples[(int)outcome];
            if (samples.Count >= SamplesPerOutcome)
                return;

            float sizeX = candidate.YawIndex == 0 ? size.x : size.z;
            float sizeZ = candidate.YawIndex == 0 ? size.z : size.x;
            samples.Add("center=(" + (candidate.Ix * VoxelSize + sizeX * 0.5f).ToString("0.00")
                + ", " + (candidate.Iz * VoxelSize + sizeZ * 0.5f).ToString("0.00") + ")"
                + " yaw=" + Yaws[candidate.YawIndex].ToString("0")
                + " floorRest=" + candidate.FloorRest.ToString("0.00")
                + " estRest=" + (candidate.EstimatedRest > candidate.FloorRest + 50f ? "on-cargo-no-room" : candidate.EstimatedRest.ToString("0.00"))
                + " ceiling=" + candidate.MinCeiling.ToString("0.00")
                + (float.IsNaN(restBottom) ? "" : " restBottom=" + restBottom.ToString("0.00"))
                + " support=" + supportHits
                + (lastSweepHit != null ? " hit=" + lastSweepHit : ""));
        }

        private static Outcome Evaluate(Candidate candidate, Vector3 size, out Placement placement, out float restBottomOut, out int supportHitsOut)
        {
            Quaternion rotation = Quaternion.Euler(0f, Yaws[candidate.YawIndex], 0f);
            float sizeX = candidate.YawIndex == 0 ? size.x : size.z;
            float sizeZ = candidate.YawIndex == 0 ? size.z : size.x;
            float x = candidate.Ix * VoxelSize + sizeX * 0.5f;
            float z = candidate.Iz * VoxelSize + sizeZ * 0.5f;
            return EvaluatePose(x, z, rotation, size, candidate.FloorRest, candidate.MinCeiling, candidate.MinFloor,
                out placement, out restBottomOut, out supportHitsOut);
        }

        // Tests the item's box, centred over (x, z) at the given (upright) rotation: dropped from just under
        // ceilingY down as far as minFloorY, then judged for support. floorRest is the lowest it could rest.
        private static Outcome EvaluatePose(float x, float z, Quaternion rotation, Vector3 size, float floorRest, float ceilingY, float minFloorY,
            out Placement placement, out float restBottomOut, out int supportHitsOut)
        {
            placement = default(Placement);
            restBottomOut = float.NaN;
            supportHitsOut = 0;
            float height = size.y;
            Vector3 sweepHalf = size * 0.5f - Vector3.one * Skin;

            // Drop the box from as high as it fits under the ceiling down past the painted floor.
            float startBottom = ceilingY - height - 0.005f;
            if (startBottom < floorRest - 0.03f)
                return Outcome.NoRoom;

            var startCenter = new Vector3(x, startBottom + height * 0.5f, z);
            float sweep = startBottom - (minFloorY - 0.1f);
            if (!SweepDown(startCenter, sweepHalf, rotation, sweep, out float distance, out bool startBlocked))
                return startBlocked ? Outcome.NoRoom : Outcome.NoSurface;

            var restCenter = new Vector3(x, startCenter.y - distance + Skin, z);
            float restBottom = restCenter.y - height * 0.5f;
            restBottomOut = restBottom;

            // Support: probe straight down under a grid across the underside.
            int hits = 0, flat = 0;
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i < SupportSamplesPerSide; i++)
            {
                for (int j = 0; j < SupportSamplesPerSide; j++)
                {
                    float u = Mathf.Lerp(-0.4f, 0.4f, i / (float)(SupportSamplesPerSide - 1)) * size.x;
                    float v = Mathf.Lerp(-0.4f, 0.4f, j / (float)(SupportSamplesPerSide - 1)) * size.z;
                    Vector3 sample = restCenter + rotation * new Vector3(u, -height * 0.5f + 0.02f, v);
                    if (!RaycastDown(sample, 0.02f + SupportProbeDepth, out Vector3 normal))
                        continue;

                    hits++;
                    if (normal.y >= MinFlatNormalY)
                        flat++;
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u);
                    minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                }
            }

            supportHitsOut = hits;
            if (hits == 0)
                return Outcome.Hanging;

            // Stable when the centre lies over the supported region and most of the contact is level; resting mostly
            // on a steep slope means it will slide.
            bool centred = minU <= 0f && maxU >= 0f && minV <= 0f && maxV >= 0f;
            if (hits < 3 || !centred || flat * 2 < hits)
                return Outcome.Unstable;

            placement = new Placement
            {
                CenterLocal = restCenter,
                RotationLocal = rotation,
                Size = size,
                RestBottomY = restBottom,
                SupportHits = hits,
                FlatHits = flat,
                SideContacts = CountSideContacts(restCenter, size, rotation)
            };
            return Outcome.Valid;
        }

        // Probes a thin slab just outside each vertical face for structure or cargo to pack against.
        private static int CountSideContacts(Vector3 center, Vector3 size, Quaternion rotation)
        {
            int contacts = 0;
            Vector3 half = size * 0.5f;
            float slabHalf = ContactGap * 0.5f;
            float slabHeight = Mathf.Max(0.01f, half.y - 0.02f);
            var faces = new[]
            {
                new KeyValuePair<Vector3, Vector3>(new Vector3(half.x + slabHalf, 0.01f, 0f), new Vector3(slabHalf, slabHeight, half.z - 0.01f)),
                new KeyValuePair<Vector3, Vector3>(new Vector3(-half.x - slabHalf, 0.01f, 0f), new Vector3(slabHalf, slabHeight, half.z - 0.01f)),
                new KeyValuePair<Vector3, Vector3>(new Vector3(0f, 0.01f, half.z + slabHalf), new Vector3(half.x - 0.01f, slabHeight, slabHalf)),
                new KeyValuePair<Vector3, Vector3>(new Vector3(0f, 0.01f, -half.z - slabHalf), new Vector3(half.x - 0.01f, slabHeight, slabHalf))
            };

            foreach (var face in faces)
                if (OverlapsObstacle(center + rotation * face.Key, face.Value, rotation))
                    contacts++;

            return contacts;
        }

        // Each collider of the item and its physics body, with its size and centre in its own frame.
        private static string DescribeColliders(ShipItem item)
        {
            var parts = new List<string>();
            foreach (var collider in item.GetComponents<Collider>())
                parts.Add(DescribeCollider("", collider));
            var body = item.GetItemRigidbody();
            if (body)
                foreach (var collider in body.GetComponentsInChildren<Collider>(true))
                    parts.Add(DescribeCollider("body:", collider));
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none";
        }

        private static string DescribeCollider(string prefix, Collider collider)
        {
            string text = prefix + collider.GetType().Name.Replace("Collider", "") + (collider.enabled ? "" : "(off)");
            if (TryGetColliderLocalBounds(collider, out Bounds local))
                text += "[size=" + Format(local.size) + " center=" + Format(local.center);
            else
                text += "[";
            if (collider is MeshCollider mesh && mesh.sharedMesh)
                text += " mesh='" + mesh.sharedMesh.name + "' verts=" + mesh.sharedMesh.vertexCount + " convex=" + mesh.convex;
            return text + "]";
        }

        // The item's shape as one box in its own frame. Its physics body is what actually collides, and can carry more
        // than the item's own collider (a barrel has a box and a convex mesh), so the box encloses all of the body's
        // colliders. The body sits exactly at the item's pose, so its frame is the item's frame.
        private static bool TryGetItemBox(ShipItem item, out Vector3 center, out Vector3 size)
        {
            var body = item.GetItemRigidbody();
            if (body && TryGetBodyBox(body, out center, out size))
                return true;
            return TryGetItemColliderBox(item, out center, out size);
        }

        private static bool TryGetBodyBox(ItemRigidbody body, out Vector3 center, out Vector3 size)
        {
            center = Vector3.zero;
            size = Vector3.zero;

            bool any = false;
            var bounds = default(Bounds);
            foreach (var collider in body.GetComponentsInChildren<Collider>(true))
            {
                if (!TryGetColliderLocalBounds(collider, out Bounds local))
                    continue;

                Vector3 e = local.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                    Vector3 point = body.transform.InverseTransformPoint(collider.transform.TransformPoint(corner));
                    if (any)
                        bounds.Encapsulate(point);
                    else
                        bounds = new Bounds(point, Vector3.zero);
                    any = true;
                }
            }

            if (!any)
                return false;

            center = bounds.center;
            size = bounds.size;
            return size.x > 0.01f && size.y > 0.01f && size.z > 0.01f;
        }

        private static bool TryGetColliderLocalBounds(Collider collider, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (collider is BoxCollider box)
            {
                bounds = new Bounds(box.center, box.size);
                return true;
            }
            if (collider is CapsuleCollider capsule)
            {
                float diameter = capsule.radius * 2f;
                var capsuleSize = new Vector3(diameter, diameter, diameter);
                capsuleSize[capsule.direction] = Mathf.Max(capsule.height, diameter);
                bounds = new Bounds(capsule.center, capsuleSize);
                return true;
            }
            if (collider is SphereCollider sphere)
            {
                bounds = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
                return true;
            }
            if (collider is MeshCollider mesh && mesh.sharedMesh)
            {
                bounds = mesh.sharedMesh.bounds;
                return true;
            }
            return false;
        }

        private static bool TryGetItemColliderBox(ShipItem item, out Vector3 center, out Vector3 size)
        {
            center = Vector3.zero;
            size = Vector3.zero;

            var box = item.GetComponent<BoxCollider>();
            var capsule = item.GetComponent<CapsuleCollider>();
            var meshCollider = item.GetComponent<MeshCollider>();
            var filter = item.GetComponent<MeshFilter>();

            if (box)
            {
                center = box.center;
                size = box.size;
            }
            else if (capsule)
            {
                center = capsule.center;
                float diameter = capsule.radius * 2f;
                size = new Vector3(diameter, diameter, diameter);
                size[capsule.direction] = Mathf.Max(capsule.height, diameter);
            }
            else if (meshCollider && meshCollider.sharedMesh)
            {
                center = meshCollider.sharedMesh.bounds.center;
                size = meshCollider.sharedMesh.bounds.size;
            }
            else if (filter && filter.sharedMesh)
            {
                center = filter.sharedMesh.bounds.center;
                size = filter.sharedMesh.bounds.size;
            }
            else
            {
                return false;
            }

            Vector3 scale = item.transform.lossyScale;
            center = Vector3.Scale(center, scale);
            size = new Vector3(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y), Mathf.Abs(size.z * scale.z));
            return size.x > 0.01f && size.y > 0.01f && size.z > 0.01f;
        }

        private static bool IsObstacle(Collider collider)
        {
            if (!collider || collider.isTrigger)
                return false;
            if (CargoAreaPainter.IsStructure(walkCol, collider))
                return true;

            // Other cargo aboard: its physics bodies live in the walk collider.
            var body = collider.GetComponentInParent<ItemRigidbody>();
            return body != null && body != selectedBody && collider.transform.IsChildOf(walkCol);
        }

        private static bool SweepDown(Vector3 localCenter, Vector3 localHalfExtents, Quaternion localRotation, float localDistance, out float hitDistance, out bool startBlocked)
        {
            hitDistance = 0f;
            startBlocked = false;
            Vector3 worldDirection = walkCol.TransformVector(Vector3.down);
            float scale = worldDirection.magnitude;

            int count = Physics.BoxCastNonAlloc(
                walkCol.TransformPoint(localCenter),
                ToWorldHalfExtents(localHalfExtents),
                worldDirection / scale,
                HitBuffer,
                walkCol.rotation * localRotation,
                localDistance * scale,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = HitBuffer[i];
                if (!IsObstacle(hit.collider))
                    continue;

                // A sweep reports colliders it starts inside at distance zero.
                if (hit.distance <= 0f)
                {
                    startBlocked = true;
                    lastSweepHit = DescribeHit(hit.collider) + "@start";
                    return false;
                }

                if (hit.distance < best)
                {
                    best = hit.distance;
                    lastSweepHit = DescribeHit(hit.collider) + "@y=" + walkCol.InverseTransformPoint(hit.point).y.ToString("0.00");
                }
            }

            if (best == float.MaxValue)
                return false;

            hitDistance = best / scale;
            return true;
        }

        private static string DescribeHit(Collider collider)
        {
            var body = collider.GetComponentInParent<ItemRigidbody>();
            if (body != null)
                return "cargo:'" + body.name + "'";
            return "structure:'" + collider.name + "'";
        }

        private static bool RaycastDown(Vector3 localOrigin, float localDistance, out Vector3 localNormal)
        {
            localNormal = Vector3.up;
            Vector3 worldDirection = walkCol.TransformVector(Vector3.down);
            float scale = worldDirection.magnitude;

            int count = Physics.RaycastNonAlloc(
                walkCol.TransformPoint(localOrigin),
                worldDirection / scale,
                HitBuffer,
                localDistance * scale,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = HitBuffer[i];
                if (hit.distance >= best || !IsObstacle(hit.collider))
                    continue;

                best = hit.distance;
                localNormal = walkCol.InverseTransformDirection(hit.normal).normalized;
            }

            return best != float.MaxValue;
        }

        private static bool OverlapsObstacle(Vector3 localCenter, Vector3 localHalfExtents, Quaternion localRotation)
        {
            int count = Physics.OverlapBoxNonAlloc(
                walkCol.TransformPoint(localCenter),
                ToWorldHalfExtents(localHalfExtents),
                OverlapBuffer,
                walkCol.rotation * localRotation,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
                if (IsObstacle(OverlapBuffer[i]))
                    return true;

            return false;
        }

        private static Vector3 ToWorldHalfExtents(Vector3 localHalfExtents)
        {
            Vector3 scale = walkCol.lossyScale;
            return new Vector3(
                Mathf.Abs(localHalfExtents.x * scale.x),
                Mathf.Abs(localHalfExtents.y * scale.y),
                Mathf.Abs(localHalfExtents.z * scale.z));
        }

        private static void ShowGhost(Transform worldBoat, Placement placement)
        {
            ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "VC_CargoSolverGhost";
            Object.Destroy(ghost.GetComponent<Collider>());
            ghost.layer = 2;

            var renderer = ghost.GetComponent<MeshRenderer>();
            if (!ghostMaterial)
                ghostMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.3f, 0.85f, 1f, 0.4f) };
            renderer.sharedMaterial = ghostMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ghost.transform.SetParent(worldBoat, false);
            ghost.transform.localPosition = placement.CenterLocal;
            ghost.transform.localRotation = placement.RotationLocal;
            ghost.transform.localScale = placement.Size;
        }

        private static void Notify(string message)
        {
            LastMessage = message;
            NotificationUi.instance?.ShowNotification(message);
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")";
        }
    }
}
