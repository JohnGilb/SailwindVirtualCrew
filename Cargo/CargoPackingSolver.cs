using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Developer spike: find where one cargo item should go in the painted cargo area, and show it as a ghost.
    ///
    /// The item is handled by its real shape, not its bounding box. At the start of a solve its physics colliders are
    /// copied onto a hidden probe. The shape is classified: round about one of its axes (a barrel) or box-like. Items are
    /// only turned in 90-degree steps, so a round item has three poses (on end, lying along the boat, lying across it)
    /// and a box-like item six (each axis up, turned two ways). The probe is measured on the 10cm grid in each pose: for
    /// each column the item reaches, the lowest and highest point of the item there. A barrel, narrow at the ends and
    /// round, only reaches the columns its round body covers, and stood on end its base is narrower than its belly.
    ///
    /// Candidates are the item in each pose with its bounds starting at every painted cell. A coarse pass
    /// keeps candidates where every column the item reaches is painted at the item's height with room to spare: the
    /// item would rest where its underside first meets a painted floor, and must fit under every ceiling. Cargo already
    /// aboard only orders the candidates. Each survivor is then tested for real in the walk collider, against the
    /// boat's structure and the cargo aboard, using the probe: it must start clear, is swept down to where it comes to
    /// rest, the surface under its base is probed to judge whether it would stay put, and its sides are swept for walls
    /// and neighbours to pack against.
    ///
    /// Choosing among stable spots, for tidy-looking holds:
    ///  - Separate painted areas are filled bottom up: every spot on the lowest level (usually the hold) is used before
    ///    anything goes on the next one up (the deck). See CargoArea.GetLevel.
    ///  - Poses come in tiers of preference, and a tier is only used once the ones before it have no stable spot
    ///    anywhere: a barrel stands on end before it lies down; a box-like item lies flat (shortest side up) with its
    ///    longest side along the ship before it is turned across, and flat before it is stood on a side or end.
    ///  - The hold fills from bow to stern: spots are grouped into bands along the ship by where the item's front would
    ///    be, and the band furthest forward wins. Within a band the lowest spot wins, then the one touching the most
    ///    sides. So a row across the bow is filled and stacked before the next row aft, and deckhands carrying cargo in
    ///    from aft mostly avoid walking through what they've already stowed.
    ///
    /// Solves run a slice at a time (SolveRoutine, driven by CargoLoadPlanner), a few milliseconds per frame, so even
    /// a large hold never stalls the game. Successive solves for the same kind of item share a plan cache (PlanCache):
    /// the candidate list is reused, re-estimated only where cargo changed, and every spot that failed is remembered and
    /// skipped until cargo changes nearby in a way that could make it work (see PlanCache.Failed). The probe, and the phantoms standing in for reserved spots, are only active
    /// during a slice; physics never steps with them present, so they can't push real cargo. Shapes are measured once
    /// per item type and cached.
    ///
    /// Look at an item (or hold it) and press the solve key to preview; press it again on the same item to move the
    /// item there, after which its drift while settling is measured.
    /// </summary>
    internal static partial class CargoPackingSolver
    {
        private const string Phase = "CargoSolve";

        private const float VoxelSize = CargoArea.CellSize;
        // Rest poses in the same band of height count as equally low.
        private const float LevelBucket = 0.05f;
        // Physics testing stops after this many candidates. Deliberately huge for now: solves run to completion, and
        // long ones are flagged in the log (LongSolveMilliseconds) so the slow cases can be found and sped up.
        private const int MaxPhysicsCandidates = 500000;
        private const float LongSolveMilliseconds = 1000f;
        // Cargo bounds are shrunk by this much so neighbouring columns a crate only touches aren't counted as covered.
        private const float CargoBoundsInset = 0.02f;
        // The square inside a circle is this fraction of the circle's bounding square across (1 / sqrt 2).
        private const float RoundFootprintScale = 0.707f;
        // Deeper than this into structure or cargo counts as starting blocked; less is resting contact.
        private const float StartPenetrationTolerance = 0.003f;
        // Confirming a planned spot allows a little more, for cargo that has settled a touch since.
        private const float ConfirmPenetrationTolerance = 0.005f;
        private static float penetrationTolerance = StartPenetrationTolerance;
        // The rest pose is checked this far above where the drop ended, clear of the surface it landed on.
        private const float RestCheckLift = 0.002f;
        // Confirming a planned spot drops the item from just above it: from exactly the pose the solve checked it at
        // (EvaluatePose starts 5mm below startY, so this puts the start at the rest pose plus RestCheckLift). Anywhere
        // else, an item touching a neighbour with a bulging or bevelled side (a barrel's belly, a crate's rim) reads
        // deeper into it, and a spot the solve accepted gets turned down.
        internal const float ConfirmStartLift = RestCheckLift + 0.005f;
        // How far the start comes down each time it turns out to be inside structure.
        private const float StructureStepDown = 0.1f;
        // Points of the underside within this of its lowest point form its base, which is what it stands on.
        private const float BaseTolerance = 0.03f;
        private const int MaxSupportSamples = 16;
        private const float SupportProbeDepth = 0.04f;
        private const float MinFlatNormalY = 0.85f;
        private const float ContactGap = 0.03f;
        private const float PlaceLift = 0.01f;
        private const float SettleSeconds = 3f;

        // A cross-section counts as round when its two widths agree to within this fraction...
        private const float RoundWidthTolerance = 0.06f;
        // ...and nothing of the item lies this far out towards the corners of its bounds (a circle reaches 0.71).
        private const float RoundCornerFraction = 0.8f;
        // Depth of the bands along the ship that the hold fills in, bow first.
        private const float FillBandDepth = 0.25f;
        // Where in each column the shape is sampled, as fractions across the cell.
        private static readonly float[] ProfileSamples = { 0.1f, 0.5f, 0.9f };

        internal enum Outcome
        {
            // The item can't start above the spot: structure or cargo is already there.
            NoRoom,
            // The drop found nothing to land on above the painted floor.
            NoSurface,
            // The drop landed on something, but none of the probes under the item's base reach it: it is caught on an
            // edge or rim (another barrel's belly, a ledge) that it would slide off.
            Hanging,
            // Supported, but not around its centre of mass, or mostly on a slope.
            Unstable,
            Valid
        }

        internal struct Placement
        {
            // The candidate it came from, so a spot that turns out not to work can be struck off (ForgetSpot).
            public long CandidateKey;
            public string PoseLabel;
            public int Level;
            public int Tier;
            // Which band along the ship, counted from stern to bow (higher is further forward).
            public int Band;
            // The item's own origin and rotation in walk-collider (boat-local) space.
            public Vector3 Position;
            public Quaternion Rotation;
            public float RestBottomY;
            public int SupportHits;
            public int SupportSamples;
            public int FlatHits;
            public int SideContacts;
        }

        private struct Candidate
        {
            // Identifies the candidate across solves of the same kind of item (see AddCandidate).
            public long Key;
            public int Ix;
            public int Iz;
            // The floor of the painted run the candidate was generated from; its reference height (see TryCoarseFit).
            public float AnchorFloorY;
            public int PoseIndex;
            public int Level;
            public int Tier;
            public int Band;
            // The origin height at which the item's underside first meets a painted floor: the lowest it can rest.
            public float FloorRest;
            // Where it would probably rest counting cargo already aboard; only orders the candidates.
            public float EstimatedRest;
            // The estimate says cargo already fills this spot up to the ceiling. Usually right, so these are only tried
            // once every other spot in the level and tier has failed.
            public bool LikelyBlocked;
            // The highest origin height at which it fits under every painted ceiling: where the drop starts.
            public float StartY;
        }

        // One way to set the item down: its rotation, from which of its own axes point up and forward.
        private sealed class Pose
        {
            public Quaternion Rotation;
            public Vector3 LocalUp;
            public Vector3 LocalForward;
            // For a round item, its axis of symmetry; zero for a box-like one.
            public Vector3 SymmetryAxis;
            public string Label;
        }

        // The item's shape measured on the grid in one pose. Offsets are relative to the item's origin, in boat axes.
        private sealed class PoseProfile
        {
            public Pose Pose;
            public Quaternion Rotation;
            // Preference: 0 is the item's preferred pose; higher tiers are only tried when lower ones find nothing.
            public int Tier;
            // The shape's bounds min corner (x, z) relative to the origin; candidates put it on a cell boundary.
            public float MinX;
            public float MinZ;
            public float MaxX;
            public float MaxZ;
            public int CellsX;
            public int CellsZ;
            // Per column (dx * CellsZ + dz): lowest and highest point of the item, or NaN where it doesn't reach.
            public float[] Bottom;
            public float[] Top;
            public float BaseY;
            public readonly List<Vector3> BaseSamples = new List<Vector3>();
            public Vector3 CenterOfMass;
            public int CoveredColumns;

            public bool Covers(int dx, int dz)
            {
                return !float.IsNaN(Bottom[dx * CellsZ + dz]);
            }
        }

        // A hidden stand-in carrying copies of the item's physics colliders, for queries with its exact shape.
        private sealed class ShapeProbe
        {
            public GameObject Root;
            public Rigidbody Body;
            public readonly List<Collider> Colliders = new List<Collider>();
            // Each collider's pose and scale relative to the probe, and its bounds in its own frame: enough to place it
            // for a query without asking Unity to sync transforms.
            public readonly List<Vector3> ColliderPositions = new List<Vector3>();
            public readonly List<Quaternion> ColliderRotations = new List<Quaternion>();
            public readonly List<Vector3> ColliderScales = new List<Vector3>();
            public readonly List<Bounds> ColliderBounds = new List<Bounds>();
            public readonly List<PoseProfile> Profiles = new List<PoseProfile>();
            public Bounds LocalBounds;
            public string Kind;
            // Round about one of its axes (a barrel) rather than box-like.
            public bool IsRound;
            public string Summary;
            // Kept in the shape cache (by prefab) rather than destroyed after use.
            public bool Cached;
        }

        /// <summary>One solve, run a slice at a time by <see cref="SolveRoutine"/>.</summary>
        internal sealed class SolveJob
        {
            public ShipItem Item;
            public bool HasPlacement;
            public Placement Best;
            public string Failure;
            // For the log: the solve's statistics line, and its time spent working (summed over slices).
            public string Summary;
            public float ActiveMilliseconds;
            public int Frames;
        }

        // One boxed piece of cargo (or a reserved spot) as the estimate saw it: where it is, and which columns it marks.
        private struct CargoBox
        {
            public Vector3 Center;
            public Quaternion Rotation;
            public int X0, X1, Z0, Z1;
            // Real cargo, as opposed to a reservation (for the log).
            public bool Real;
        }

        // What successive solves for one kind of item can share, as long as the boat, the painted area (and its stack
        // height) and the bow haven't changed.
        private sealed class PlanCache
        {
            public Transform WalkCol;
            public int AreaVersion = -1;
            public Vector3 BowAxis;
            public int PoseCount;
            public bool Valid;
            public readonly List<Candidate> Candidates = new List<Candidate>();
            // The cargo the cached candidates' estimates were made against.
            public Dictionary<long, CargoBox> Cargo = new Dictionary<long, CargoBox>();
            // Candidates that failed, and whether they failed as blocked (true) or otherwise (false: hanging, unstable,
            // nothing to land on, or a confirmation that didn't hold). Each is skipped until cargo changes over its
            // footprint in a way that could make it work: a blocked spot only when cargo there is removed or moves
            // (adding cargo can't unblock anything); any other failure when anything there changes (new cargo may hold
            // it up). Reserved spots count as cargo here, so releasing one reopens the spots it blocked.
            public readonly Dictionary<long, bool> Failed = new Dictionary<long, bool>();
        }

        private static readonly Dictionary<int, PlanCache> PlanCaches = new Dictionary<int, PlanCache>();
        private const int PlanCacheLimit = 16;
        // Cargo moved less than this (or turned less than MovedAngle) hasn't changed for the estimate.
        private const float MovedDistance = 0.02f;
        private const float MovedAngle = 2f;
        // Keys for reserved spots in the cargo snapshot, clear of item instance ids.
        private const long ReservationKeyBase = 1L << 40;

        // The cargo aboard (and reserved spots) at the start of the current solve.
        private static Dictionary<long, CargoBox> currentCargo = new Dictionary<long, CargoBox>();
        // Since the plan cache's snapshot: every box that appeared, went away or moved (at old and new places), and
        // just the ones that went away or moved (at their old places).
        private static readonly List<CargoBox> ChangedCargo = new List<CargoBox>();
        private static readonly List<CargoBox> RemovedCargo = new List<CargoBox>();
        private static int remembered;
        private static int reestimated;
        private static bool candidatesReused;

        // Shapes by item prefab: measuring one takes a few milliseconds, and cargo comes in many of each kind.
        private static readonly Dictionary<int, ShapeProbe> ShapeCache = new Dictionary<int, ShapeProbe>();
        private const int ShapeCacheLimit = 64;

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[64];
        // Big enough that a crowded hold (hull, deck planks, beams and many crates around one spot) never fills it.
        private static readonly Collider[] OverlapBuffer = new Collider[256];
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
        // Towards the bow, along whichever horizontal boat axis is the ship's length (+/- x or z, boat-local).
        private static Vector3 bowAxis = Vector3.forward;
        private static ItemRigidbody selectedBody;
        private static ShipItem selectedItem;
        private static ShapeProbe probe;
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
        private static float shapeMilliseconds;
        private static float candidatesMilliseconds;
        internal static float LastSolveMilliseconds { get; private set; }
        internal static string LastMessage { get; private set; }

        internal static int CountOf(Outcome outcome)
        {
            return OutcomeCounts[(int)outcome];
        }

        private static readonly Stopwatch SliceWatch = new Stopwatch();
        private static float sliceBudgetMilliseconds = float.MaxValue;
        private static float activeBeforeSlice;
        private static bool inPhysics;
        private static bool previousBackfaces;

        /// <summary>Starts a slice of solving: work continues until the budget is spent, then the routine yields.</summary>
        internal static void BeginSlice(float budgetMilliseconds, float activeSoFar)
        {
            sliceBudgetMilliseconds = budgetMilliseconds;
            activeBeforeSlice = activeSoFar;
            SliceWatch.Reset();
            SliceWatch.Start();
        }

        internal static float SliceMilliseconds => (float)SliceWatch.Elapsed.TotalMilliseconds;
        private static bool SliceExpired => SliceWatch.Elapsed.TotalMilliseconds > sliceBudgetMilliseconds;
        private static float ActiveMilliseconds => activeBeforeSlice + SliceMilliseconds;

        // Physics work needs back faces (single-sided decks), the probe, and the phantoms of other items' reserved spots.
        // None of it may stay switched on across a frame, when physics steps.
        private static void EnterPhysics()
        {
            if (inPhysics)
                return;
            inPhysics = true;
            previousBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            CargoLoadPlanner.ActivatePhantoms(walkCol, selectedItem);
            if (probe != null && probe.Root)
                probe.Root.SetActive(true);
            Physics.SyncTransforms();
        }

        private static void LeavePhysics()
        {
            if (!inPhysics)
                return;
            inPhysics = false;
            if (probe != null && probe.Root)
                probe.Root.SetActive(false);
            CargoLoadPlanner.DeactivatePhantoms();
            Physics.queriesHitBackfaces = previousBackfaces;
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

        /// <summary>Developer preview: plans a spot for the item (without reserving it) and shows it as a ghost.</summary>
        internal static void Solve(ShipItem item)
        {
            ClearPreview();
            SelectedItem = item;
            if (!item)
                return;

            Notify("Planning a spot for '" + item.name + "'...");
            CargoLoadPlanner.Request(item, reserve: false, onDone: (job, reservation) =>
            {
                if (SelectedItem != item)
                    return;

                var context = CrewBoatContextResolver.Resolve();
                if (!job.HasPlacement || context == null)
                {
                    Notify(job.Failure ?? ("No stable spot for '" + item.name + "' in the painted area."));
                    return;
                }

                HasPlacement = true;
                Best = job.Best;
                ShowGhost(context.WorldBoat, item, job.Best);
                LastMessage = "Best spot for '" + item.name + "' (" + job.Best.PoseLabel + "): rest " + job.Best.RestBottomY.ToString("0.00")
                    + "m, support " + job.Best.SupportHits + "/" + job.Best.SupportSamples
                    + ", " + job.Best.SideContacts + " side" + (job.Best.SideContacts == 1 ? "" : "s") + " touching.";
                NotificationUi.instance?.ShowNotification(LastMessage);
            });
        }

        /// <summary>
        /// Plans a spot for <paramref name="job"/>'s item, a slice at a time: yields whenever the slice budget set by
        /// <see cref="BeginSlice"/> is spent. The result (and the statistics line for the log) is left on the job.
        /// </summary>
        internal static IEnumerator SolveRoutine(SolveJob job)
        {
            var item = job.Item;
            HasPlacement = false;
            CandidateCount = 0;
            probeMoves = 0;
            MoveWatch.Reset();
            PhysicsEvaluated = 0;
            BudgetExhausted = false;
            System.Array.Clear(OutcomeCounts, 0, OutcomeCounts.Length);
            foreach (var samples in OutcomeSamples)
                samples.Clear();

            var context = CrewBoatContextResolver.Resolve();
            var area = CargoAreaPainter.GetActiveArea();
            if (!item || context == null || area == null || area.RunCount == 0)
            {
                job.Failure = "Paint a cargo area on this boat first.";
                yield break;
            }

            walkCol = context.WalkCol;
            selectedItem = item;
            selectedBody = item.GetItemRigidbody();
            if (!selectedBody)
            {
                job.Failure = "'" + item.name + "' has no physics body yet.";
                yield break;
            }

            bowAxis = ResolveBowAxis(context);
            bool shapeBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool haveShape;
            try
            {
                haveShape = AcquireShape(item, selectedBody);
            }
            finally
            {
                Physics.queriesHitBackfaces = shapeBackfaces;
            }

            if (!haveShape)
            {
                job.Failure = "Can't work out the shape of '" + item.name + "'.";
                yield break;
            }

            AssignTiers();
            lastTierSummary = DescribeTiers();
            shapeMilliseconds = ActiveMilliseconds;

            BuildCargoColumns();
            // The painted runs as they are now; painting may carry on while the solve runs.
            var anchors = new List<CargoArea.PaintedRun>(area.EnumerateRuns());

            EnterPhysics();
            try
            {
                // Candidates: every painted cell, in every pose (see TryCoarseFit). When this kind of item was solved
                // before on the same painted area, reuse that list and estimate again only candidates over cargo that
                // has changed since.
                var cache = GetPlanCache(item, area);
                Candidates.Clear();
                SeenCandidates.Clear();
                remembered = 0;
                reestimated = 0;
                candidatesReused = cache != null && cache.Valid;
                if (candidatesReused)
                {
                    DiffCargo(cache.Cargo, currentCargo);

                    Candidates.AddRange(cache.Candidates);
                    for (int i = 0; i < Candidates.Count; i++)
                    {
                        if ((i & 255) == 0 && SliceExpired)
                        {
                            LeavePhysics();
                            yield return null;
                            if (!item || !walkCol)
                            {
                                job.Failure = "The item or the boat went away while planning.";
                                yield break;
                            }
                            EnterPhysics();
                        }

                        var candidate = Candidates[i];
                        if (!Touches(candidate, ChangedCargo))
                            continue;

                        Candidates[i] = Reestimate(area, candidate);
                        // Something changed over this spot: forget a failure it could have undone.
                        if (cache.Failed.TryGetValue(candidate.Key, out bool blocked) && (!blocked || Touches(candidate, RemovedCargo)))
                            cache.Failed.Remove(candidate.Key);
                    }
                }
                else
                {
                    cache?.Failed.Clear();
                    for (int poseIndex = 0; poseIndex < probe.Profiles.Count; poseIndex++)
                    {
                        var profile = probe.Profiles[poseIndex];
                        for (int i = 0; i < anchors.Count; i++)
                        {
                            if ((i & 63) == 0 && SliceExpired)
                            {
                                LeavePhysics();
                                yield return null;
                                if (!item || !walkCol)
                                {
                                    job.Failure = "The item or the boat went away while planning.";
                                    yield break;
                                }
                                EnterPhysics();
                            }

                            AddCandidate(area, profile, poseIndex, anchors[i]);
                        }
                    }
                }

                if (cache != null)
                {
                    cache.Candidates.Clear();
                    cache.Candidates.AddRange(Candidates);
                    cache.Cargo = new Dictionary<long, CargoBox>(currentCargo);
                    cache.Valid = true;
                }

                // Lowest level first, then preferred poses, then spots not already full of cargo, then bow first,
                // then lowest first.
                Candidates.Sort((a, b) =>
                {
                    if (a.Level != b.Level)
                        return a.Level.CompareTo(b.Level);
                    if (a.Tier != b.Tier)
                        return a.Tier.CompareTo(b.Tier);
                    if (a.LikelyBlocked != b.LikelyBlocked)
                        return a.LikelyBlocked.CompareTo(b.LikelyBlocked);
                    if (a.Band != b.Band)
                        return b.Band.CompareTo(a.Band);
                    return a.EstimatedRest.CompareTo(b.EstimatedRest);
                });
                CandidateCount = Candidates.Count;
                candidatesMilliseconds = ActiveMilliseconds - shapeMilliseconds;

                // Physics, candidate by candidate.
                float bestScore = float.MaxValue;
                var best = default(Placement);
                for (int i = 0; i < Candidates.Count; i++)
                {
                    if (SliceExpired)
                    {
                        LeavePhysics();
                        yield return null;
                        if (!item || !walkCol)
                        {
                            job.Failure = "The item or the boat went away while planning.";
                            yield break;
                        }
                        EnterPhysics();
                    }

                    var candidate = Candidates[i];
                    if (HasPlacement)
                    {
                        // A higher level, a later tier, or a spot that looks full are only for when nothing else was
                        // found; candidates are sorted that way, then bow first, so once they fall behind the best spot's
                        // band nothing later can beat it.
                        if (candidate.Level != best.Level || candidate.Tier != best.Tier || candidate.LikelyBlocked
                            || candidate.Band < best.Band)
                            break;

                        // Nothing can rest below its painted floor, so within the band a candidate whose floor is
                        // clearly higher than the best stable spot found can't beat it.
                        if (candidate.FloorRest + probe.Profiles[candidate.PoseIndex].BaseY > best.RestBottomY + LevelBucket + 0.05f)
                            continue;
                    }

                    if (PhysicsEvaluated >= MaxPhysicsCandidates)
                    {
                        BudgetExhausted = true;
                        break;
                    }

                    if (cache != null && cache.Failed.ContainsKey(candidate.Key))
                    {
                        remembered++;
                        continue;
                    }

                    PhysicsEvaluated++;
                    lastSweepHit = null;
                    var outcome = Evaluate(candidate, out var placement, out float restY, out int supportHits);
                    OutcomeCounts[(int)outcome]++;
                    if (outcome != Outcome.Valid)
                    {
                        if (cache != null)
                            cache.Failed[candidate.Key] = outcome == Outcome.NoRoom;
                        RecordSample(outcome, candidate, restY, supportHits);
                        continue;
                    }

                    placement.CandidateKey = candidate.Key;
                    placement.Level = candidate.Level;
                    placement.Tier = candidate.Tier;
                    placement.Band = candidate.Band;
                    float score = -placement.Band * 1000000f
                        + Mathf.Floor(placement.RestBottomY / LevelBucket) * 1000f
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
                job.HasPlacement = HasPlacement;
                job.Best = best;
                if (!HasPlacement)
                    job.Failure = "No stable spot for '" + item.name + "' in the painted area.";
            }
            finally
            {
                LeavePhysics();
                ReleaseProbe();
            }

            float total = ActiveMilliseconds;
            job.Summary = "probeMoves=" + probeMoves + " moveMs=" + MoveWatch.Elapsed.TotalMilliseconds.ToString("0.0")
                + " (" + (UseRigidbodyMoves ? "rigidbody" : "transform+sync") + ")"
                + " candidates=" + CandidateCount
                + (candidatesReused ? " (reused, " + reestimated + " re-estimated)" : " (built)")
                + " physics=" + PhysicsEvaluated
                + " remembered=" + remembered
                + " noRoom=" + CountOf(Outcome.NoRoom)
                + " noSurface=" + CountOf(Outcome.NoSurface)
                + " hanging=" + CountOf(Outcome.Hanging)
                + " unstable=" + CountOf(Outcome.Unstable)
                + " valid=" + CountOf(Outcome.Valid)
                + " activeMs=" + total.ToString("0.0")
                + " (shape " + shapeMilliseconds.ToString("0") + ", candidates " + candidatesMilliseconds.ToString("0")
                + ", physics " + (total - shapeMilliseconds - candidatesMilliseconds).ToString("0") + ")"
                + (BudgetExhausted ? " CANDIDATE-LIMIT-REACHED" : "")
                + " cargoColumns=" + CargoColumns.Count
                + " reservations=" + CargoLoadPlanner.ReservationCount
                + " shape=" + lastShapeSummary
                + " tiers=[" + lastTierSummary + "]";
        }

        /// <summary>Logs a finished solve, with how long it worked and over how many frames.</summary>
        internal static void LogSolve(SolveJob job)
        {
            var itemBody = job.Item ? job.Item.GetItemRigidbody() : null;
            string name = job.Item ? job.Item.name : "(gone)";
            string timing = " itemBody#" + (itemBody ? itemBody.GetInstanceID() : 0)
                + " frames=" + job.Frames + " activeMs=" + job.ActiveMilliseconds.ToString("0");
            if (job.ActiveMilliseconds > LongSolveMilliseconds)
                CrewDebugLog.Warn(Phase, "LONG SOLVE item='" + name + "'" + timing + " " + job.Summary);

            if (!job.HasPlacement)
            {
                CrewDebugLog.Ok(Phase, "Solve item='" + name + "' FAILED (" + job.Failure + ")" + timing + " " + job.Summary);
                LogOutcomeSamples();
                return;
            }

            var best = job.Best;
            CrewDebugLog.Ok(Phase,
                "Solve item='" + name + "'"
                + " best origin=" + Format(best.Position)
                + " level=" + best.Level + " pose='" + best.PoseLabel + "' tier=" + best.Tier
                + " band=" + best.Band + " (bow " + AxisLabel(bowAxis) + ")"
                + " restBottom=" + best.RestBottomY.ToString("0.000")
                + " support=" + best.SupportHits + "/" + best.SupportSamples + " flat=" + best.FlatHits
                + " contacts=" + best.SideContacts
                + timing + " " + job.Summary);
            LogOutcomeSamples();
        }

        /// <summary>
        /// Checks, now, that the item would still come to rest at a planned pose: dropped from just above it, it lands
        /// within a few cm and is stable. Cheap (one evaluation); safe to call between slices of a solve in progress.
        /// </summary>
        internal static bool ValidatePose(ShipItem item, Vector3 position, Quaternion rotation, out string detail)
        {
            detail = null;
            var context = CrewBoatContextResolver.Resolve();
            var body = item ? item.GetItemRigidbody() : null;
            if (context == null || !body)
            {
                detail = "no boat or no physics body";
                return false;
            }

            // A solve may be part-way through; keep its state intact around this check.
            var savedWalkCol = walkCol;
            var savedItem = selectedItem;
            var savedBody = selectedBody;
            var savedProbe = probe;
            var savedSweepHit = lastSweepHit;
            bool wasInPhysics = inPhysics;
            if (wasInPhysics)
                LeavePhysics();

            walkCol = context.WalkCol;
            selectedItem = item;
            selectedBody = body;
            probe = null;
            try
            {
                bool shapeBackfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;
                bool haveShape;
                try
                {
                    haveShape = AcquireShape(item, body);
                }
                finally
                {
                    Physics.queriesHitBackfaces = shapeBackfaces;
                }

                if (!haveShape)
                {
                    detail = "unknown shape";
                    return false;
                }

                PoseProfile profile = null;
                foreach (var candidate in probe.Profiles)
                    if (Quaternion.Angle(candidate.Rotation, rotation) < 1f)
                        profile = candidate;

                EnterPhysics();
                try
                {
                    if (profile == null)
                    {
                        var rigidbody = body.GetBody();
                        profile = MeasureProfile(probe, rotation, rigidbody ? rigidbody.centerOfMass : probe.LocalBounds.center);
                    }

                    lastSweepHit = null;
                    penetrationTolerance = ConfirmPenetrationTolerance;
                    var outcome = EvaluatePose(position.x, position.z, profile, position.y - 0.3f, position.y + ConfirmStartLift,
                        out _, out float restY, out int support);
                    bool valid = outcome == Outcome.Valid && Mathf.Abs(restY - position.y) <= 0.06f;
                    detail = outcome + (float.IsNaN(restY) ? "" : " rest " + (restY - position.y).ToString("+0.00;-0.00") + "m")
                        + " support " + support + (lastSweepHit != null ? " hit " + lastSweepHit : "");
                    return valid;
                }
                finally
                {
                    penetrationTolerance = StartPenetrationTolerance;
                    LeavePhysics();
                    ReleaseProbe();
                }
            }
            finally
            {
                walkCol = savedWalkCol;
                selectedItem = savedItem;
                selectedBody = savedBody;
                probe = savedProbe;
                lastSweepHit = savedSweepHit;
                if (wasInPhysics)
                    EnterPhysics();
            }
        }

        /// <summary>
        /// Whether the item is round (barrel-like) or box-like, and the volume of its bounds: from the shape cache, or
        /// measured now (a few milliseconds, once per kind of item). Safe to call between slices of a solve in progress.
        /// </summary>
        internal static bool TryClassifyShape(ShipItem item, out bool round, out float volume)
        {
            round = false;
            volume = 0f;
            var context = CrewBoatContextResolver.Resolve();
            var body = item ? item.GetItemRigidbody() : null;
            if (context == null || !body)
                return false;

            var savedWalkCol = walkCol;
            var savedItem = selectedItem;
            var savedBody = selectedBody;
            var savedProbe = probe;
            bool wasInPhysics = inPhysics;
            if (wasInPhysics)
                LeavePhysics();

            walkCol = context.WalkCol;
            selectedItem = item;
            selectedBody = body;
            probe = null;
            bool shapeBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                if (!AcquireShape(item, body))
                    return false;

                round = probe.IsRound;
                Vector3 size = probe.LocalBounds.size;
                volume = size.x * size.y * size.z;
                ReleaseProbe();
                return true;
            }
            finally
            {
                Physics.queriesHitBackfaces = shapeBackfaces;
                walkCol = savedWalkCol;
                selectedItem = savedItem;
                selectedBody = savedBody;
                probe = savedProbe;
                if (wasInPhysics)
                    EnterPhysics();
            }
        }

        /// <summary>
        /// A hidden copy of the item's colliders, for standing in for it at a reserved spot. Inactive; the planner
        /// switches it on only while solving.
        /// </summary>
        internal static GameObject CreatePhantom(ShipItem item, out Bounds localBounds)
        {
            localBounds = default(Bounds);
            var body = item ? item.GetItemRigidbody() : null;
            if (!body)
                return null;

            var savedProbe = probe;
            var savedWalkCol = walkCol;
            try
            {
                var context = CrewBoatContextResolver.Resolve();
                if (context == null)
                    return null;
                walkCol = context.WalkCol;
                probe = null;
                if (!AcquireShape(item, body))
                    return null;

                var phantom = Object.Instantiate(probe.Root);
                phantom.name = "VC_CargoReservation_" + item.name;
                var rigidbody = phantom.GetComponent<Rigidbody>();
                if (rigidbody)
                    Object.DestroyImmediate(rigidbody);
                localBounds = probe.LocalBounds;
                ReleaseProbe();
                return phantom;
            }
            finally
            {
                probe = savedProbe;
                walkCol = savedWalkCol;
            }
        }

        private static string lastTierSummary;

        private static string DescribeTiers()
        {
            if (probe == null)
                return "";
            var parts = new List<string>();
            foreach (var profile in probe.Profiles)
                parts.Add(profile.Pose.Label + "=" + profile.Tier);
            return string.Join(", ", parts.ToArray());
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
            Quaternion rotation = best.Rotation;
            Vector3 position = best.Position + Vector3.up * PlaceLift;

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

        // ---------------------------------------------------------------- shape

        private static string lastShapeSummary;

        // The item's shape: from the cache when this kind of item has been measured before, otherwise measured now
        // (and cached by prefab). Sets probe.
        private static bool AcquireShape(ShipItem item, ItemRigidbody body)
        {
            ReleaseProbe();
            int prefab = item ? item.GetPrefabIndex() : -1;
            if (prefab >= 0 && ShapeCache.TryGetValue(prefab, out var cached) && cached.Root)
            {
                probe = cached;
                lastShapeSummary = cached.Summary + " (cached)";
                return true;
            }

            if (!BuildProbe(body))
            {
                ReleaseProbe();
                return false;
            }

            if (prefab >= 0)
            {
                if (ShapeCache.Count >= ShapeCacheLimit)
                    ClearShapeCache();
                probe.Cached = true;
                ShapeCache[prefab] = probe;
            }
            return true;
        }

        internal static void ClearShapeCache()
        {
            foreach (var shape in ShapeCache.Values)
                if (shape.Root && shape != probe)
                    Object.Destroy(shape.Root);
            ShapeCache.Clear();
        }

        // Copies the body's colliders onto a hidden probe, works out its poses, and measures its profile in each.
        private static bool BuildProbe(ItemRigidbody body)
        {
            ReleaseProbe();

            var root = new GameObject("VC_CargoSolverProbe");
            root.SetActive(false);
            root.layer = 2;
            var shape = new ShapeProbe { Root = root };
            shape.Body = root.AddComponent<Rigidbody>();
            shape.Body.isKinematic = true;
            shape.Body.useGravity = false;

            bool anyBounds = false;
            foreach (var source in body.GetComponentsInChildren<Collider>(true))
            {
                var copy = CopyCollider(source, body.transform, root.transform);
                if (!copy)
                    continue;

                shape.Colliders.Add(copy);
                shape.ColliderPositions.Add(copy.transform.localPosition);
                shape.ColliderRotations.Add(copy.transform.localRotation);
                shape.ColliderScales.Add(copy.transform.localScale);
                TryGetColliderLocalBounds(copy, out Bounds copyBounds);
                shape.ColliderBounds.Add(copyBounds);
                if (TryGetColliderLocalBounds(source, out Bounds local))
                {
                    Vector3 e = local.extents;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                        Vector3 point = body.transform.InverseTransformPoint(source.transform.TransformPoint(corner));
                        if (anyBounds)
                            shape.LocalBounds.Encapsulate(point);
                        else
                            shape.LocalBounds = new Bounds(point, Vector3.zero);
                        anyBounds = true;
                    }
                }
            }

            probe = shape;
            if (shape.Colliders.Count == 0 || !anyBounds)
                return false;

            var rigidbody = body.GetBody();
            Vector3 centerOfMass = rigidbody ? rigidbody.centerOfMass : shape.LocalBounds.center;

            root.SetActive(true);
            try
            {
                foreach (var pose in BuildPoses(shape))
                {
                    var profile = MeasureProfile(shape, pose.Rotation, centerOfMass);
                    profile.Pose = pose;
                    if (profile.CoveredColumns > 0 && profile.BaseSamples.Count > 0)
                        shape.Profiles.Add(profile);
                }
            }
            finally
            {
                root.SetActive(false);
            }

            var parts = new List<string>();
            foreach (var profile in shape.Profiles)
                parts.Add(profile.Pose.Label + ":" + profile.CoveredColumns + "/" + (profile.CellsX * profile.CellsZ)
                    + "cols,base" + profile.BaseSamples.Count);
            shape.Summary = shape.Kind + ", " + shape.Colliders.Count + " colliders, bounds " + Format(shape.LocalBounds.size)
                + ", poses [" + string.Join(" ", parts.ToArray()) + "]";
            lastShapeSummary = shape.Summary;
            return shape.Profiles.Count > 0;
        }

        private static readonly Vector3[] LocalAxes = { Vector3.up, Vector3.right, Vector3.forward };

        // The ship's forward (its rigidbody's +z, which the game's rudder and momentum treat as ahead) in boat-local
        // space, snapped to the nearest horizontal axis. The boat model's own axes can be turned relative to the ship's.
        private static Vector3 ResolveBowAxis(CrewBoatContext context)
        {
            var rigidbody = context.Rigidbody;
            Transform ship = rigidbody ? rigidbody.transform : context.TopBoat;
            Vector3 forward = context.WorldBoat.InverseTransformDirection(ship.forward);
            return Mathf.Abs(forward.x) > Mathf.Abs(forward.z)
                ? new Vector3(Mathf.Sign(forward.x), 0f, 0f)
                : new Vector3(0f, 0f, Mathf.Sign(forward.z));
        }

        private static string AxisLabel(Vector3 axis)
        {
            return (axis.x + axis.y + axis.z > 0f ? "+" : "-") + AxisName(new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)));
        }

        // Preference tiers. A round item: on end (0), then lying (1). A box-like item: shortest side up with its longest
        // side along the ship (0), shortest side up the other way round (1), then any other pose (2).
        private static void AssignTiers()
        {
            Vector3 size = probe.LocalBounds.size;
            Vector3 shortest = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
            Vector3 longest = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            float shortestLength = Vector3.Dot(size, shortest);
            float longestLength = Vector3.Dot(size, longest);
            Vector3 shipLength = new Vector3(Mathf.Abs(bowAxis.x), 0f, Mathf.Abs(bowAxis.z));

            foreach (var profile in probe.Profiles)
            {
                var pose = profile.Pose;
                if (pose.SymmetryAxis != Vector3.zero)
                {
                    profile.Tier = pose.LocalUp == pose.SymmetryAxis ? 0 : 1;
                    continue;
                }

                // Sides of equal length are interchangeable, so compare by length rather than by axis.
                bool flat = Mathf.Abs(Vector3.Dot(size, pose.LocalUp) - shortestLength) < 0.01f;
                Vector3 alongShip = Quaternion.Inverse(pose.Rotation) * shipLength;
                bool lengthwise = Mathf.Abs(Mathf.Abs(Vector3.Dot(size, alongShip)) - longestLength) < 0.01f;
                profile.Tier = flat ? (lengthwise ? 0 : 1) : 2;
            }
        }

        // The band along the ship holding an item whose origin is at (x, z): by where its front (bow-most) face is.
        private static int FillBand(PoseProfile profile, float x, float z)
        {
            float front;
            if (bowAxis.x > 0f) front = x + profile.MaxX;
            else if (bowAxis.x < 0f) front = -(x + profile.MinX);
            else if (bowAxis.z > 0f) front = z + profile.MaxZ;
            else front = -(z + profile.MinZ);
            return Mathf.FloorToInt(front / FillBandDepth);
        }

        // The poses to try: three for an item round about one of its axes, six for a box-like one.
        private static List<Pose> BuildPoses(ShapeProbe shape)
        {
            var poses = new List<Pose>();
            if (TryFindSymmetryAxis(shape, out Vector3 axis, out Vector3 across))
            {
                shape.Kind = "round about " + AxisName(axis);
                shape.IsRound = true;
                poses.Add(MakePose(axis, Vector3.Cross(across, axis), axis, "on end"));
                poses.Add(MakePose(across, Vector3.Cross(axis, across), axis, "lying along x"));
                poses.Add(MakePose(across, axis, axis, "lying along z"));
                return poses;
            }

            shape.Kind = "box-like";
            foreach (var up in LocalAxes)
                foreach (var alongX in LocalAxes)
                    if (alongX != up)
                        poses.Add(MakePose(up, Vector3.Cross(alongX, up), Vector3.zero,
                            AxisName(up) + " up, " + AxisName(alongX) + " along x"));
            return poses;
        }

        // A pose that turns the item's localUp to point up and its localForward along the boat's z axis.
        private static Pose MakePose(Vector3 localUp, Vector3 localForward, Vector3 symmetryAxis, string label)
        {
            return new Pose
            {
                Rotation = Quaternion.Inverse(Quaternion.LookRotation(localForward, localUp)),
                LocalUp = localUp,
                LocalForward = localForward,
                SymmetryAxis = symmetryAxis,
                Label = label
            };
        }

        // Round about an axis when the two widths across it match and the corners of its bounds, seen along the axis,
        // are empty (a square cross-section would fill them). Checked with rays along the axis through the probe's own
        // colliders, with the probe at the walk-collider origin in the item's own orientation.
        private static bool TryFindSymmetryAxis(ShapeProbe shape, out Vector3 axis, out Vector3 across)
        {
            MoveProbe(Vector3.zero, Quaternion.identity);
            Vector3 size = shape.LocalBounds.size;
            Vector3 center = shape.LocalBounds.center;

            foreach (var candidate in LocalAxes)
            {
                Vector3 p = candidate == Vector3.right ? Vector3.up : Vector3.right;
                Vector3 q = Vector3.Cross(candidate, p);
                float widthP = Mathf.Abs(Vector3.Dot(size, p));
                float widthQ = Mathf.Abs(Vector3.Dot(size, q));
                float length = Mathf.Abs(Vector3.Dot(size, candidate));
                if (Mathf.Abs(widthP - widthQ) > RoundWidthTolerance * Mathf.Max(widthP, widthQ))
                    continue;

                bool cornerFilled = false;
                for (int corner = 0; corner < 4 && !cornerFilled; corner++)
                {
                    Vector3 offset = p * (widthP * 0.5f * RoundCornerFraction * ((corner & 1) == 0 ? -1f : 1f))
                        + q * (widthQ * 0.5f * RoundCornerFraction * ((corner & 2) == 0 ? -1f : 1f));
                    Vector3 start = center + offset - candidate * (length * 0.5f + 1f);
                    cornerFilled = RaycastShape(shape, start, candidate, length + 2f, out _);
                }

                if (!cornerFilled)
                {
                    axis = candidate;
                    across = p;
                    return true;
                }
            }

            axis = Vector3.zero;
            across = Vector3.zero;
            return false;
        }

        private static string AxisName(Vector3 axis)
        {
            return axis == Vector3.up ? "y" : axis == Vector3.right ? "x" : "z";
        }

        private static Collider CopyCollider(Collider source, Transform bodyFrame, Transform probeRoot)
        {
            var holder = new GameObject("shape");
            holder.layer = 2;
            holder.transform.SetParent(probeRoot, false);
            holder.transform.localPosition = bodyFrame.InverseTransformPoint(source.transform.position);
            holder.transform.localRotation = Quaternion.Inverse(bodyFrame.rotation) * source.transform.rotation;
            Vector3 scale = source.transform.lossyScale;
            Vector3 bodyScale = bodyFrame.lossyScale;
            holder.transform.localScale = new Vector3(scale.x / bodyScale.x, scale.y / bodyScale.y, scale.z / bodyScale.z);

            switch (source)
            {
                case BoxCollider box:
                    var boxCopy = holder.AddComponent<BoxCollider>();
                    boxCopy.center = box.center;
                    boxCopy.size = box.size;
                    return boxCopy;
                case CapsuleCollider capsule:
                    var capsuleCopy = holder.AddComponent<CapsuleCollider>();
                    capsuleCopy.center = capsule.center;
                    capsuleCopy.radius = capsule.radius;
                    capsuleCopy.height = capsule.height;
                    capsuleCopy.direction = capsule.direction;
                    return capsuleCopy;
                case SphereCollider sphere:
                    var sphereCopy = holder.AddComponent<SphereCollider>();
                    sphereCopy.center = sphere.center;
                    sphereCopy.radius = sphere.radius;
                    return sphereCopy;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    var meshCopy = holder.AddComponent<MeshCollider>();
                    meshCopy.sharedMesh = mesh.sharedMesh;
                    // A body's mesh colliders are convex (ItemRigidbody makes them so); keep it that way for sweeps.
                    meshCopy.convex = true;
                    return meshCopy;
                default:
                    Object.Destroy(holder);
                    return null;
            }
        }

        // Measures the shape on the grid in one orientation, with the probe active and its origin at the walk-collider origin.
        private static PoseProfile MeasureProfile(ShapeProbe shape, Quaternion rotation, Vector3 centerOfMass)
        {
            MoveProbe(Vector3.zero, rotation);

            // The shape's bounds in boat axes relative to its origin.
            var bounds = new Bounds(rotation * shape.LocalBounds.center, Vector3.zero);
            Vector3 e = shape.LocalBounds.extents;
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(rotation * (shape.LocalBounds.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));

            var profile = new PoseProfile
            {
                Rotation = rotation,
                MinX = bounds.min.x,
                MinZ = bounds.min.z,
                MaxX = bounds.max.x,
                MaxZ = bounds.max.z,
                CellsX = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / VoxelSize - 0.01f)),
                CellsZ = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / VoxelSize - 0.01f)),
                CenterOfMass = rotation * centerOfMass
            };
            profile.Bottom = new float[profile.CellsX * profile.CellsZ];
            profile.Top = new float[profile.CellsX * profile.CellsZ];
            profile.BaseY = float.MaxValue;

            float rayLength = bounds.size.y + 2f;
            for (int dx = 0; dx < profile.CellsX; dx++)
            {
                for (int dz = 0; dz < profile.CellsZ; dz++)
                {
                    float bottom = float.MaxValue, top = float.MinValue;
                    foreach (float fx in ProfileSamples)
                    {
                        foreach (float fz in ProfileSamples)
                        {
                            float x = profile.MinX + (dx + fx) * VoxelSize;
                            float z = profile.MinZ + (dz + fz) * VoxelSize;
                            if (RaycastShape(shape, new Vector3(x, bounds.min.y - 1f, z), Vector3.up, rayLength, out float up))
                                bottom = Mathf.Min(bottom, bounds.min.y - 1f + up);
                            if (RaycastShape(shape, new Vector3(x, bounds.max.y + 1f, z), Vector3.down, rayLength, out float down))
                                top = Mathf.Max(top, bounds.max.y + 1f - down);
                        }
                    }

                    int index = dx * profile.CellsZ + dz;
                    if (bottom == float.MaxValue || top == float.MinValue)
                    {
                        profile.Bottom[index] = float.NaN;
                        profile.Top[index] = float.NaN;
                        continue;
                    }

                    profile.Bottom[index] = bottom;
                    profile.Top[index] = top;
                    profile.BaseY = Mathf.Min(profile.BaseY, bottom);
                    profile.CoveredColumns++;
                }
            }

            // The base: the columns whose underside is within a few cm of the lowest point, spread over MaxSupportSamples.
            var basePoints = new List<Vector3>();
            for (int dx = 0; dx < profile.CellsX; dx++)
                for (int dz = 0; dz < profile.CellsZ; dz++)
                {
                    float bottom = profile.Bottom[dx * profile.CellsZ + dz];
                    if (!float.IsNaN(bottom) && bottom <= profile.BaseY + BaseTolerance)
                        basePoints.Add(new Vector3(profile.MinX + (dx + 0.5f) * VoxelSize, bottom, profile.MinZ + (dz + 0.5f) * VoxelSize));
                }

            float stride = Mathf.Max(1f, basePoints.Count / (float)MaxSupportSamples);
            for (float i = 0f; i < basePoints.Count && profile.BaseSamples.Count < MaxSupportSamples; i += stride)
                profile.BaseSamples.Add(basePoints[(int)i]);

            return profile;
        }

        // A ray against the probe's own colliders only, in walk-collider space; returns the local distance to the nearest.
        private static bool RaycastShape(ShapeProbe shape, Vector3 localOrigin, Vector3 localDirection, float localDistance, out float distance)
        {
            distance = float.MaxValue;
            Vector3 worldDirection = walkCol.TransformVector(localDirection);
            float scale = worldDirection.magnitude;
            var ray = new Ray(walkCol.TransformPoint(localOrigin), worldDirection / scale);
            foreach (var collider in shape.Colliders)
                if (collider.Raycast(ray, out RaycastHit hit, localDistance * scale))
                    distance = Mathf.Min(distance, hit.distance / scale);
            return distance != float.MaxValue;
        }

        /// <summary>
        /// Moves the probe by its kinematic rigidbody (true), which takes effect for queries at once, instead of by its
        /// transform followed by Physics.SyncTransforms (false), which re-syncs every moved transform in the scene. A
        /// developer toggle, to compare the two with the move timings in the solve log.
        /// </summary>
        internal static bool UseRigidbodyMoves { get; set; } = true;

        private static readonly Stopwatch MoveWatch = new Stopwatch();
        private static int probeMoves;
        private static Vector3 probePosition;
        private static Quaternion probeRotation = Quaternion.identity;

        private static void MoveProbe(Vector3 localPosition, Quaternion localRotation)
        {
            probePosition = walkCol.TransformPoint(localPosition);
            probeRotation = walkCol.rotation * localRotation;
            probeMoves++;
            MoveWatch.Start();
            if (UseRigidbodyMoves && probe.Root.activeInHierarchy)
            {
                probe.Body.position = probePosition;
                probe.Body.rotation = probeRotation;
            }
            else
            {
                probe.Root.transform.SetPositionAndRotation(probePosition, probeRotation);
                Physics.SyncTransforms();
            }
            MoveWatch.Stop();
        }

        // Where one of the probe's colliders is now, from the probe's pose (its transform may be stale; see MoveProbe).
        private static void GetProbeColliderPose(int index, out Vector3 position, out Quaternion rotation, out Bounds worldBounds)
        {
            position = probePosition + probeRotation * probe.ColliderPositions[index];
            rotation = probeRotation * probe.ColliderRotations[index];
            Bounds local = probe.ColliderBounds[index];
            Vector3 scale = probe.ColliderScales[index];
            Vector3 e = local.extents;
            worldBounds = new Bounds(position + rotation * Vector3.Scale(local.center, scale), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                worldBounds.Encapsulate(position + rotation * Vector3.Scale(corner, scale));
            }
        }

        // Lets go of the current shape; one not kept in the cache is destroyed.
        private static void ReleaseProbe()
        {
            if (probe != null && !probe.Cached && probe.Root)
                Object.Destroy(probe.Root);
            probe = null;
        }

        // ---------------------------------------------------------------- candidates

        // The plan cache for this kind of item, marked invalid (to be rebuilt) if what it depends on has changed. Null for
        // an item without a prefab.
        private static PlanCache GetPlanCache(ShipItem item, CargoArea area)
        {
            int prefab = item.GetPrefabIndex();
            if (prefab < 0)
                return null;

            if (!PlanCaches.TryGetValue(prefab, out var cache))
            {
                if (PlanCaches.Count >= PlanCacheLimit)
                    PlanCaches.Clear();
                cache = new PlanCache();
                PlanCaches[prefab] = cache;
            }

            if (cache.WalkCol != walkCol || cache.AreaVersion != area.Version || cache.BowAxis != bowAxis
                || cache.PoseCount != probe.Profiles.Count)
            {
                cache.Valid = false;
                cache.WalkCol = walkCol;
                cache.AreaVersion = area.Version;
                cache.BowAxis = bowAxis;
                cache.PoseCount = probe.Profiles.Count;
            }

            return cache;
        }

        /// <summary>
        /// A planned spot for this kind of item failed its confirmation: strike it off, so the next solve moves on to
        /// another spot instead of picking this one again. It comes back when anything changes over it.
        /// </summary>
        internal static void ForgetSpot(ShipItem item, long candidateKey)
        {
            int prefab = item ? item.GetPrefabIndex() : -1;
            if (prefab >= 0 && PlanCaches.TryGetValue(prefab, out var cache) && cache.Valid)
                cache.Failed[candidateKey] = false;
        }

        /// <summary>Forgets every plan cache (a save was loaded).</summary>
        internal static void ClearPlanCaches()
        {
            PlanCaches.Clear();
        }

        // Fills ChangedCargo (every box that appeared, went away or moved between two cargo snapshots, at both its old
        // and new place) and RemovedCargo (those that went away or moved, at their old place).
        private static void DiffCargo(Dictionary<long, CargoBox> before, Dictionary<long, CargoBox> after)
        {
            ChangedCargo.Clear();
            RemovedCargo.Clear();
            foreach (var pair in before)
            {
                if (after.TryGetValue(pair.Key, out var now) && !HasMoved(pair.Value, now))
                    continue;

                ChangedCargo.Add(pair.Value);
                RemovedCargo.Add(pair.Value);
            }

            foreach (var pair in after)
                if (!before.TryGetValue(pair.Key, out var then) || HasMoved(then, pair.Value))
                    ChangedCargo.Add(pair.Value);
        }

        private static bool HasMoved(CargoBox a, CargoBox b)
        {
            return Vector3.Distance(a.Center, b.Center) > MovedDistance || Quaternion.Angle(a.Rotation, b.Rotation) > MovedAngle;
        }

        // Whether any column under the candidate's bounds is covered by one of the boxes.
        private static bool Touches(Candidate candidate, List<CargoBox> boxes)
        {
            var profile = probe.Profiles[candidate.PoseIndex];
            int x0 = candidate.Ix, x1 = candidate.Ix + profile.CellsX - 1;
            int z0 = candidate.Iz, z1 = candidate.Iz + profile.CellsZ - 1;
            foreach (var box in boxes)
                if (box.X0 <= x1 && box.X1 >= x0 && box.Z0 <= z1 && box.Z1 >= z0)
                    return true;
            return false;
        }

        // The candidate's cargo estimate made again against the cargo as it is now. Its painted-area values can't have
        // changed (the cache is only used while the painted area is the same).
        private static Candidate Reestimate(CargoArea area, Candidate candidate)
        {
            reestimated++;
            var profile = probe.Profiles[candidate.PoseIndex];
            if (TryCoarseFit(area, profile, candidate.Ix, candidate.Iz, candidate.AnchorFloorY - profile.BaseY,
                    out float floorRest, out float estimatedRest, out _))
            {
                candidate.EstimatedRest = estimatedRest;
                candidate.LikelyBlocked = estimatedRest > floorRest + 50f;
            }
            return candidate;
        }

        // One candidate: the item in this pose with its bounds starting at the anchor's cell; see TryCoarseFit.
        private static void AddCandidate(CargoArea area, PoseProfile profile, int poseIndex, CargoArea.PaintedRun anchor)
        {
            if (!TryCoarseFit(area, profile, anchor.Ix, anchor.Iz, anchor.Run.FloorY - profile.BaseY,
                    out float floorRest, out float estimatedRest, out float startY))
                return;

            long key = ((long)(anchor.Ix & 0xFFFFF) << 40) | ((long)(anchor.Iz & 0xFFFFF) << 20)
                | ((long)poseIndex << 16) | (long)(Mathf.RoundToInt(floorRest / LevelBucket) & 0xFFFF);
            if (!SeenCandidates.Add(key))
                return;

            Candidates.Add(new Candidate
            {
                Key = key,
                Ix = anchor.Ix,
                Iz = anchor.Iz,
                AnchorFloorY = anchor.Run.FloorY,
                PoseIndex = poseIndex,
                Level = area.GetLevel(anchor.Ix, anchor.Iz, anchor.Run.FloorY),
                Tier = profile.Tier,
                Band = FillBand(profile, anchor.Ix * VoxelSize - profile.MinX, anchor.Iz * VoxelSize - profile.MinZ),
                FloorRest = floorRest,
                EstimatedRest = estimatedRest,
                LikelyBlocked = estimatedRest > floorRest + 50f,
                StartY = startY
            });
        }

        // With the item's bounds starting at cell (ix, iz) and its origin near referenceY, every column the item reaches
        // must have a painted run at the item's height there. The item rests where its underside first meets one of those
        // floors (FloorRest), and must fit under all their ceilings (StartY). All heights are of the item's origin.
        private static bool TryCoarseFit(CargoArea area, PoseProfile profile, int ix, int iz, float referenceY,
            out float floorRest, out float estimatedRest, out float startY)
        {
            floorRest = float.MinValue;
            estimatedRest = float.MinValue;
            startY = float.MaxValue;

            for (int dx = 0; dx < profile.CellsX; dx++)
            {
                for (int dz = 0; dz < profile.CellsZ; dz++)
                {
                    int index = dx * profile.CellsZ + dz;
                    float bottom = profile.Bottom[index];
                    if (float.IsNaN(bottom))
                        continue;
                    float top = profile.Top[index];

                    int cx = ix + dx, cz = iz + dz;
                    if (!area.TryGetColumn(cx, cz, out var runs))
                    {
                        lastCoarseFailure = "column (" + cx + ", " + cz + ") not painted";
                        return false;
                    }

                    // The run in this column that best overlaps the item's slice there.
                    float sliceMin = referenceY + bottom, sliceMax = referenceY + top;
                    bool found = false;
                    float bestOverlap = 0f;
                    var chosen = default(CargoArea.Run);
                    foreach (var run in runs)
                    {
                        float overlap = Mathf.Min(run.CeilingY, sliceMax) - Mathf.Max(run.FloorY, sliceMin);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            chosen = run;
                            found = true;
                        }
                    }

                    if (!found)
                    {
                        lastCoarseFailure = "column (" + cx + ", " + cz + ") painted only at other heights";
                        return false;
                    }

                    // The painted area ignores cargo; estimate the item resting on top of any cargo in this run.
                    float ceiling = area.EffectiveCeiling(chosen);
                    float cargoFloor = chosen.FloorY;
                    if (CargoColumns.TryGetValue(CargoArea.Key(cx, cz), out var cargo))
                        foreach (var interval in cargo)
                            if (interval.y > cargoFloor && interval.x < ceiling)
                                cargoFloor = interval.y;

                    floorRest = Mathf.Max(floorRest, chosen.FloorY - bottom);
                    estimatedRest = Mathf.Max(estimatedRest, cargoFloor - bottom);
                    startY = Mathf.Min(startY, ceiling - top);
                }
            }

            if (floorRest == float.MinValue)
            {
                lastCoarseFailure = "the item reaches no columns";
                return false;
            }

            // Feasibility is judged on the painted space alone; cargo only reorders candidates and physics decides. When
            // the estimate says the item would have to go on top of cargo that leaves no headroom, try it last.
            if (floorRest > startY + 0.01f)
            {
                lastCoarseFailure = "no headroom: rests at " + floorRest.ToString("0.00") + " but must start below " + startY.ToString("0.00");
                return false;
            }

            if (estimatedRest > startY + 0.01f)
                estimatedRest = floorRest + 100f;
            return true;
        }

        // Records the boat-local height interval of every cargo item aboard (except the one being solved) in each column
        // its bounds cover, so the coarse pass can order candidates by where they would probably rest.
        // Also records each box in currentCargo, for the plan cache to see what changed between solves.
        private static void BuildCargoColumns()
        {
            CargoColumns.Clear();
            currentCargo = new Dictionary<long, CargoBox>();
            foreach (var reservation in CargoLoadPlanner.Reservations)
            {
                if (reservation.Item == selectedItem || reservation.WalkCol != walkCol)
                    continue;

                Vector3 center = reservation.Position + reservation.Rotation * reservation.LocalBounds.center;
                var box = MarkCargoBox(center, reservation.Rotation, reservation.LocalBounds.size);
                box.Real = false;
                currentCargo[ReservationKeyBase + reservation.Item.GetInstanceID()] = box;
            }

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

                // The item's box in walk-collider space. A barrel (known round from the shape cache) doesn't fill its
                // box's corners, so it claims only the square inside its round body.
                var shipItem = body.GetShipItem();
                int prefab = shipItem ? shipItem.GetPrefabIndex() : -1;
                bool round = prefab >= 0 && ShapeCache.TryGetValue(prefab, out var shape) && shape.IsRound;
                var box = MarkCargoBox(walkCol.InverseTransformPoint(body.transform.TransformPoint(boxCenter)),
                    Quaternion.Inverse(walkCol.rotation) * body.transform.rotation, boxSize,
                    round ? RoundFootprintScale : 1f);
                box.Real = true;
                currentCargo[body.GetInstanceID()] = box;
            }
        }

        // Records a (turned) box of cargo in every column whose centre lies inside its footprint (scaled by
        // footprintScale about its centre). Returns the box and the columns it could mark.
        private static CargoBox MarkCargoBox(Vector3 center, Quaternion rotation, Vector3 boxSize, float footprintScale = 1f)
        {
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
                    if (Mathf.Abs(inBox.x) > half.x * footprintScale - CargoBoundsInset
                        || Mathf.Abs(inBox.z) > half.z * footprintScale - CargoBoundsInset)
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

            return new CargoBox { Center = center, Rotation = rotation, X0 = x0, X1 = x1, Z0 = z0, Z1 = z1 };
        }

        // ---------------------------------------------------------------- physics

        private static Vector2 CandidateOrigin(Candidate candidate)
        {
            var profile = probe.Profiles[candidate.PoseIndex];
            return new Vector2(candidate.Ix * VoxelSize - profile.MinX, candidate.Iz * VoxelSize - profile.MinZ);
        }

        private static void RecordSample(Outcome outcome, Candidate candidate, float restY, int supportHits)
        {
            var samples = OutcomeSamples[(int)outcome];
            if (samples.Count >= SamplesPerOutcome)
                return;

            var profile = probe.Profiles[candidate.PoseIndex];
            Vector2 origin = CandidateOrigin(candidate);
            samples.Add("origin=(" + origin.x.ToString("0.00") + ", " + origin.y.ToString("0.00") + ")"
                + " pose='" + profile.Pose.Label + "'"
                + " floorRestBottom=" + (candidate.FloorRest + profile.BaseY).ToString("0.00")
                + " estRest=" + (candidate.EstimatedRest > candidate.FloorRest + 50f ? "on-cargo-no-room" : (candidate.EstimatedRest + profile.BaseY).ToString("0.00"))
                + " startBottom=" + (candidate.StartY + profile.BaseY).ToString("0.00")
                + (float.IsNaN(restY) ? "" : " restBottom=" + (restY + profile.BaseY).ToString("0.00"))
                + " support=" + supportHits + "/" + profile.BaseSamples.Count
                + (lastSweepHit != null ? " hit=" + lastSweepHit : ""));
        }

        private static Outcome Evaluate(Candidate candidate, out Placement placement, out float restYOut, out int supportHitsOut)
        {
            Vector2 origin = CandidateOrigin(candidate);
            return EvaluatePose(origin.x, origin.y, probe.Profiles[candidate.PoseIndex], candidate.FloorRest, candidate.StartY,
                out placement, out restYOut, out supportHitsOut);
        }

        // Tests the item (as the active probe) with its origin over (x, z) in the profile's orientation: it must start clear
        // at startY, is swept down no further than a little below floorRest, and is then judged for support.
        private static Outcome EvaluatePose(float x, float z, PoseProfile profile, float floorRest, float startY,
            out Placement placement, out float restYOut, out int supportHitsOut)
        {
            placement = default(Placement);
            restYOut = float.NaN;
            supportHitsOut = 0;

            float y0 = startY - 0.005f;
            if (y0 < floorRest - 0.03f)
                return Outcome.NoRoom;

            // A capped top only says where painting stopped: unpainted structure (a deck) may lie below the stack
            // height. If the start is inside structure, come down in steps until it's clear. Cargo in the way means no.
            MoveProbe(new Vector3(x, y0, z), profile.Rotation);
            while (StartsBlocked(out bool byStructure))
            {
                if (!byStructure || y0 - StructureStepDown < floorRest - 0.03f)
                    return Outcome.NoRoom;
                y0 -= StructureStepDown;
                MoveProbe(new Vector3(x, y0, z), profile.Rotation);
            }

            if (!SweepProbe(Vector3.down, y0 - (floorRest - 0.1f), out float distance))
                return Outcome.NoSurface;

            float restY = y0 - distance;
            restYOut = restY;

            // A sweep can't see anything it starts out touching (such hits are dropped), so when the start is resting
            // against the top of a stack it falls straight through it. Check the rest pose itself for anything inside.
            MoveProbe(new Vector3(x, restY + RestCheckLift, z), profile.Rotation);
            if (StartsBlocked(out _))
                return Outcome.NoRoom;

            // Support: probe straight down under the item's base.
            int hits = 0, flat = 0;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var sample in profile.BaseSamples)
            {
                var point = new Vector3(x + sample.x, restY + sample.y + 0.02f, z + sample.z);
                if (!RaycastDown(point, 0.02f + SupportProbeDepth, out Vector3 normal))
                    continue;

                hits++;
                if (normal.y >= MinFlatNormalY)
                    flat++;
                minX = Mathf.Min(minX, sample.x); maxX = Mathf.Max(maxX, sample.x);
                minZ = Mathf.Min(minZ, sample.z); maxZ = Mathf.Max(maxZ, sample.z);
            }

            supportHitsOut = hits;
            if (hits == 0)
                return Outcome.Hanging;

            // Stable when the centre of mass lies over the supported region and most of the contact is level; resting
            // mostly on a steep slope means it will slide.
            float slack = VoxelSize * 0.5f;
            bool centred = profile.CenterOfMass.x >= minX - slack && profile.CenterOfMass.x <= maxX + slack
                && profile.CenterOfMass.z >= minZ - slack && profile.CenterOfMass.z <= maxZ + slack;
            if (hits < Mathf.Min(3, profile.BaseSamples.Count) || !centred || flat * 2 < hits)
                return Outcome.Unstable;

            MoveProbe(new Vector3(x, restY + 0.002f, z), profile.Rotation);
            placement = new Placement
            {
                PoseLabel = profile.Pose != null ? profile.Pose.Label : "as placed",
                Position = new Vector3(x, restY, z),
                Rotation = profile.Rotation,
                RestBottomY = restY + profile.BaseY,
                SupportHits = hits,
                SupportSamples = profile.BaseSamples.Count,
                FlatHits = flat,
                SideContacts = CountSideContacts()
            };
            return Outcome.Valid;
        }

        // True when any of the probe's colliders is already inside structure or cargo by more than resting contact.
        private static bool StartsBlocked(out bool byStructure)
        {
            byStructure = false;
            for (int s = 0; s < probe.Colliders.Count; s++)
            {
                var shape = probe.Colliders[s];
                GetProbeColliderPose(s, out Vector3 shapePosition, out Quaternion shapeRotation, out Bounds bounds);
                int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, OverlapBuffer, Quaternion.identity,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    var other = OverlapBuffer[i];
                    if (!IsObstacle(other))
                        continue;

                    if (Physics.ComputePenetration(
                            shape, shapePosition, shapeRotation,
                            other, other.transform.position, other.transform.rotation,
                            out _, out float depth)
                        && depth > penetrationTolerance)
                    {
                        lastSweepHit = DescribeHit(other) + "@start depth=" + depth.ToString("0.000");
                        byStructure = CargoAreaPainter.IsStructure(walkCol, other);
                        return true;
                    }
                }
            }

            return false;
        }

        // Sweeps the probe's real shape; returns the local distance to the nearest structure or cargo it meets.
        private static bool SweepProbe(Vector3 localDirection, float localDistance, out float hitDistance)
        {
            hitDistance = 0f;
            Vector3 worldDirection = walkCol.TransformVector(localDirection);
            float scale = worldDirection.magnitude;
            var hits = probe.Body.SweepTestAll(worldDirection / scale, localDistance * scale, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            foreach (var hit in hits)
            {
                if (hit.distance >= best || !IsObstacle(hit.collider))
                    continue;

                best = hit.distance;
                lastSweepHit = DescribeHit(hit.collider) + "@y=" + walkCol.InverseTransformPoint(hit.point).y.ToString("0.00");
            }

            if (best == float.MaxValue)
                return false;

            hitDistance = best / scale;
            return true;
        }

        // Sweeps the probe a short way along each horizontal boat axis for walls and neighbours to pack against.
        private static int CountSideContacts()
        {
            int contacts = 0;
            string hit = lastSweepHit;
            foreach (var direction in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                if (SweepProbe(direction, ContactGap, out _))
                    contacts++;
            lastSweepHit = hit;
            return contacts;
        }

        // Structure and cargo aboard block the item and hold it up. So does a reserved spot's phantom: the item it stands
        // for will be there (stowed items stay frozen until loading is done, so it doesn't matter which arrives first).
        private static bool IsObstacle(Collider collider)
        {
            if (!collider || collider.isTrigger)
                return false;
            if (CargoLoadPlanner.IsPhantom(collider))
                return true;
            if (CargoAreaPainter.IsStructure(walkCol, collider))
                return true;

            // Other cargo aboard: its physics bodies live in the walk collider.
            var body = collider.GetComponentInParent<ItemRigidbody>();
            return body != null && body != selectedBody && collider.transform.IsChildOf(walkCol);
        }

        private static string DescribeHit(Collider collider)
        {
            if (CargoLoadPlanner.IsPhantom(collider))
                return "reserved:'" + collider.transform.root.name + "'";
            var body = collider.GetComponentInParent<ItemRigidbody>();
            if (body != null)
                return "cargo:'" + body.name + "'#" + body.GetInstanceID() + (body == selectedBody ? "(self)" : "");
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

        // ---------------------------------------------------------------- shape helpers

        // The box enclosing all of a physics body's colliders, in the body's (and so the item's) own frame.
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

        // ---------------------------------------------------------------- ghost

        // A translucent copy of the item's visible meshes at the placement.
        private static void ShowGhost(Transform worldBoat, ShipItem item, Placement placement)
        {
            if (!ghostMaterial)
                ghostMaterial = CreateGhostMaterial(new Color(0.3f, 0.85f, 1f, 0.4f));
            ghost = CreateGhost("VC_CargoSolverGhost", worldBoat, item, placement.Position, placement.Rotation, ghostMaterial);
        }

        internal static Material CreateGhostMaterial(Color color)
        {
            return new Material(Shader.Find("Sprites/Default")) { color = color };
        }

        /// <summary>
        /// A translucent copy of the item's visible meshes at a boat-local pose on <paramref name="worldBoat"/>: how the
        /// item will look once it's there. Rendering only; no colliders.
        /// </summary>
        internal static GameObject CreateGhost(string name, Transform worldBoat, ShipItem item, Vector3 position, Quaternion rotation, Material material)
        {
            var copy = new GameObject(name);
            copy.layer = 2;
            copy.transform.SetParent(worldBoat, false);
            copy.transform.localPosition = position;
            copy.transform.localRotation = rotation;

            foreach (var filter in item.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (!filter.sharedMesh || !renderer || !renderer.enabled)
                    continue;

                var part = new GameObject("part");
                part.layer = 2;
                part.transform.SetParent(copy.transform, false);
                part.transform.localPosition = item.transform.InverseTransformPoint(filter.transform.position);
                part.transform.localRotation = Quaternion.Inverse(item.transform.rotation) * filter.transform.rotation;
                part.transform.localScale = filter.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var partRenderer = part.AddComponent<MeshRenderer>();
                partRenderer.sharedMaterial = material;
                partRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                partRenderer.receiveShadows = false;
            }

            return copy;
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
