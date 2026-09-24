using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Plans where cargo goes, without stalling the game, and keeps planned spots from being handed out twice.
    ///
    /// Plan requests queue up and are solved one at a time (CargoPackingSolver.SolveRoutine), a slice per frame within
    /// a small time budget, so planning a whole shipment spreads over frames instead of freezing the game. Because only
    /// one solve runs at once, and a finished solve's spot is reserved before the next begins, two requests can never be
    /// given the same spot.
    ///
    /// A reservation is phantom cargo: a hidden copy of the item's colliders at its planned pose. Later solves treat it
    /// as cargo, both in the way and to stand on, so the plan can stack on items that haven't arrived yet. Deliveries
    /// mostly land in plan order (bottom and bow first); one that lands early hangs in place, because stowed items stay
    /// frozen until loading is finished (see FrozenCargo). Phantoms are only switched on during solver work, never across
    /// a frame, so physics never sees them.
    ///
    /// Each reserved spot shows a ghost of its item (a translucent copy) until the item is set down there or the spot
    /// is released, so the player can watch the plan fill the hold ahead of the deckhands.
    ///
    /// The ship keeps changing while a solve runs (cargo settles, the player moves things, deliveries land), so a solve's
    /// winning spot is confirmed as it finishes, and again by the deckhand on arrival (<see cref="Confirm"/>).
    /// </summary>
    internal static class CargoLoadPlanner
    {
        private const string Phase = "CargoPlan";
        // Solver work per frame; more when a deckhand is standing there waiting on the plan.
        private const float FrameBudgetMilliseconds = 3f;
        private const float UrgentFrameBudgetMilliseconds = 8f;
        // A solve whose spot is gone by the time it finishes is retried this many times.
        private const int MaxAttempts = 3;
        private static readonly Color GhostColor = new Color(0.55f, 1f, 0.6f, 0.3f);
        private static Material ghostMaterial;

        internal sealed class Reservation
        {
            public ShipItem Item;
            public Transform WalkCol;
            // The item's origin and rotation in walk-collider (boat-local) space.
            public Vector3 Position;
            public Quaternion Rotation;
            public string PoseLabel;
            public long CandidateKey;
            public Bounds LocalBounds;
            public GameObject Phantom;
            // What the player sees: a translucent copy of the item at the spot.
            public GameObject Ghost;
            public readonly List<Collider> Colliders = new List<Collider>();
        }

        private sealed class Job
        {
            public CargoPackingSolver.SolveJob Solve;
            public bool Reserve;
            public bool Urgent;
            public bool Cancelled;
            public int Attempts;
            public IEnumerator Routine;
            public Action<CargoPackingSolver.SolveJob, Reservation> OnDone;
        }

        private static readonly LinkedList<Job> Pending = new LinkedList<Job>();
        private static readonly Dictionary<ShipItem, Reservation> ReservationsByItem = new Dictionary<ShipItem, Reservation>();
        private static readonly HashSet<Collider> PhantomColliders = new HashSet<Collider>();
        private static Job current;
        private static bool phantomsActive;

        internal static bool IsBusy => current != null;
        internal static int QueueLength => Pending.Count + (current != null ? 1 : 0);
        internal static int ReservationCount => ReservationsByItem.Count;
        internal static IEnumerable<Reservation> Reservations => ReservationsByItem.Values;
        internal static string CurrentItemName => current != null && current.Solve.Item ? current.Solve.Item.name : null;
        internal static int CurrentFrames => current != null ? current.Solve.Frames : 0;

        /// <summary>
        /// Queues a plan for <paramref name="item"/>; <paramref name="onDone"/> runs when it finishes, with the reservation
        /// made (when <paramref name="reserve"/>) or null if there's no room. Replaces any plan already queued for the item,
        /// and releases its reservation, so it is planned afresh. An <paramref name="urgent"/> plan (a deckhand is standing
        /// there holding the item) goes to the front of the queue, next after the solve already running.
        /// </summary>
        internal static void Request(ShipItem item, bool reserve, Action<CargoPackingSolver.SolveJob, Reservation> onDone, bool urgent = false)
        {
            if (!item)
                return;

            Cancel(item);
            var job = new Job
            {
                Solve = new CargoPackingSolver.SolveJob { Item = item },
                Reserve = reserve,
                Urgent = urgent,
                OnDone = onDone
            };

            if (urgent)
                Pending.AddFirst(job);
            else
                Pending.AddLast(job);
        }

        /// <summary>
        /// A deckhand is now waiting on this item's plan: move it to the front of the queue (or, if it's running, give it
        /// more time each frame).
        /// </summary>
        internal static void Expedite(ShipItem item)
        {
            if (current != null && current.Solve.Item == item)
            {
                current.Urgent = true;
                return;
            }

            for (var node = Pending.First; node != null; node = node.Next)
            {
                if (node.Value.Solve.Item != item)
                    continue;

                node.Value.Urgent = true;
                Pending.Remove(node);
                Pending.AddFirst(node);
                return;
            }
        }

        /// <summary>Drops any queued or running plan for the item and releases its reservation.</summary>
        internal static void Cancel(ShipItem item)
        {
            for (var node = Pending.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Solve.Item == item)
                    Pending.Remove(node);
                node = next;
            }

            if (current != null && current.Solve.Item == item)
                current.Cancelled = true;

            Release(item);
        }

        internal static bool TryGetReservation(ShipItem item, out Reservation reservation)
        {
            reservation = null;
            return item && ReservationsByItem.TryGetValue(item, out reservation);
        }

        internal static bool IsPlanning(ShipItem item)
        {
            if (current != null && current.Solve.Item == item && !current.Cancelled)
                return true;
            foreach (var job in Pending)
                if (job.Solve.Item == item)
                    return true;
            return false;
        }

        /// <summary>Confirms, now, that the item would still come to rest at its reserved spot.</summary>
        internal static bool Confirm(Reservation reservation, out string detail)
        {
            detail = null;
            if (reservation == null || !reservation.Item)
                return false;

            bool valid = CargoPackingSolver.ValidatePose(reservation.Item, reservation.Position, reservation.Rotation, out detail);
            if (!valid)
            {
                CrewDebugLog.Ok(Phase, "Reserved spot for '" + reservation.Item.name + "' no longer works: " + detail);
                CargoPackingSolver.ForgetSpot(reservation.Item, reservation.CandidateKey);
            }
            return valid;
        }

        internal static void Release(ShipItem item)
        {
            if (!item || !ReservationsByItem.TryGetValue(item, out var reservation))
                return;

            ReservationsByItem.Remove(item);
            foreach (var collider in reservation.Colliders)
                PhantomColliders.Remove(collider);
            if (reservation.Phantom)
                UnityEngine.Object.Destroy(reservation.Phantom);
            if (reservation.Ghost)
                UnityEngine.Object.Destroy(reservation.Ghost);
        }

        /// <summary>Forgets every plan and reservation (a save was loaded, or the boat changed).</summary>
        internal static void Clear()
        {
            Pending.Clear();
            if (current != null)
            {
                (current.Routine as IDisposable)?.Dispose();
                current = null;
            }

            foreach (var item in new List<ShipItem>(ReservationsByItem.Keys))
                Release(item);
            ReservationsByItem.Clear();
            PhantomColliders.Clear();
            CargoPackingSolver.ClearPlanCaches();
        }

        internal static bool IsPhantom(Collider collider)
        {
            return phantomsActive && PhantomColliders.Contains(collider);
        }

        /// <summary>Switches on the phantoms of every reserved spot on this boat except the given item's own.</summary>
        internal static void ActivatePhantoms(Transform walkCol, ShipItem except)
        {
            if (!walkCol)
                return;

            foreach (var reservation in ReservationsByItem.Values)
            {
                if (!reservation.Phantom || reservation.Item == except || reservation.WalkCol != walkCol)
                    continue;

                // The walk collider can move (floating origin), so place the phantom afresh each time.
                reservation.Phantom.transform.SetPositionAndRotation(
                    walkCol.TransformPoint(reservation.Position), walkCol.rotation * reservation.Rotation);
                reservation.Phantom.SetActive(true);
            }

            phantomsActive = true;
        }

        internal static void DeactivatePhantoms()
        {
            foreach (var reservation in ReservationsByItem.Values)
                if (reservation.Phantom)
                    reservation.Phantom.SetActive(false);
            phantomsActive = false;
        }

        /// <summary>Runs a slice of the current solve; starts the next queued one when it's done. Every frame.</summary>
        internal static void Tick()
        {
            if (current == null)
            {
                if (Pending.Count == 0)
                    return;

                current = Pending.First.Value;
                Pending.RemoveFirst();
                current.Routine = CargoPackingSolver.SolveRoutine(current.Solve);
            }

            var job = current;
            if (job.Cancelled || !job.Solve.Item)
            {
                (job.Routine as IDisposable)?.Dispose();
                current = null;
                // An item that vanished (sold, destroyed) still gets its answer, so whoever asked isn't left waiting.
                if (!job.Cancelled)
                {
                    job.Solve.Failure = "The item is gone.";
                    job.OnDone?.Invoke(job.Solve, null);
                }
                return;
            }

            CargoPackingSolver.BeginSlice(job.Urgent ? UrgentFrameBudgetMilliseconds : FrameBudgetMilliseconds, job.Solve.ActiveMilliseconds);
            bool running;
            try
            {
                running = job.Routine.MoveNext();
            }
            catch (Exception ex)
            {
                CrewDebugLog.Fail(Phase, "Solve for '" + job.Solve.Item.name + "' failed: " + ex);
                job.Solve.Failure = "Planning failed: " + ex.GetType().Name;
                running = false;
            }

            job.Solve.ActiveMilliseconds += CargoPackingSolver.SliceMilliseconds;
            job.Solve.Frames++;
            if (running)
                return;

            current = null;
            (job.Routine as IDisposable)?.Dispose();
            Finish(job);
        }

        private static void Finish(Job job)
        {
            var solve = job.Solve;
            CargoPackingSolver.LogSolve(solve);
            if (job.Cancelled || !solve.Item)
                return;

            Reservation reservation = null;
            if (solve.HasPlacement && job.Reserve)
            {
                // The solve took several frames; make sure the spot is still good before handing it out.
                if (!CargoPackingSolver.ValidatePose(solve.Item, solve.Best.Position, solve.Best.Rotation, out string detail))
                {
                    // Struck off, so the retry picks another spot even if this one only failed here and not in the solve.
                    CargoPackingSolver.ForgetSpot(solve.Item, solve.Best.CandidateKey);
                    if (++job.Attempts < MaxAttempts)
                    {
                        CrewDebugLog.Ok(Phase, "Planned spot for '" + solve.Item.name + "' changed while planning (" + detail + "); planning again.");
                        job.Solve = new CargoPackingSolver.SolveJob { Item = solve.Item };
                        job.Routine = null;
                        Pending.AddFirst(job);
                        return;
                    }

                    solve.HasPlacement = false;
                    solve.Failure = "The cargo area kept changing while planning '" + solve.Item.name + "'.";
                }
                else
                {
                    reservation = Reserve(solve.Item, solve.Best);
                }
            }

            job.OnDone?.Invoke(solve, reservation);
        }

        private static Reservation Reserve(ShipItem item, CargoPackingSolver.Placement placement)
        {
            Release(item);
            var context = CrewBoatContextResolver.Resolve();
            var phantom = CargoPackingSolver.CreatePhantom(item, out Bounds localBounds);
            var reservation = new Reservation
            {
                Item = item,
                WalkCol = context != null ? context.WalkCol : null,
                Position = placement.Position,
                Rotation = placement.Rotation,
                PoseLabel = placement.PoseLabel,
                CandidateKey = placement.CandidateKey,
                LocalBounds = localBounds,
                Phantom = phantom
            };

            if (phantom)
            {
                phantom.SetActive(false);
                foreach (var collider in phantom.GetComponentsInChildren<Collider>(true))
                {
                    reservation.Colliders.Add(collider);
                    PhantomColliders.Add(collider);
                }
            }

            if (context != null && context.WorldBoat)
            {
                if (!ghostMaterial)
                    ghostMaterial = CargoPackingSolver.CreateGhostMaterial(GhostColor);
                reservation.Ghost = CargoPackingSolver.CreateGhost("VC_CargoReservationGhost_" + item.name,
                    context.WorldBoat, item, placement.Position, placement.Rotation, ghostMaterial);
            }

            ReservationsByItem[item] = reservation;
            CrewDebugLog.Ok(Phase, "Reserved spot for '" + item.name + "' (" + placement.PoseLabel + ") at " + placement.Position
                + "; " + ReservationsByItem.Count + " reserved, " + QueueLength + " still planning.");
            return reservation;
        }
    }
}
