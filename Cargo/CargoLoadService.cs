using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Marking cargo on the dock to be loaded aboard: which items qualify, their LOAD stamp, and queueing the deckhand
    /// request (<see cref="HaulLoadRequest"/>).
    ///
    /// Loading everything at once (the Supercargo window) finds all the player's cargo near the boat that isn't aboard,
    /// works out each kind's shape a kind per frame (so a dock full of new goods doesn't stall the game), and queues the
    /// loads box-like cargo first, then barrels, biggest first and like with like. Loads are planned and handed out in
    /// that order, so crates fill the bow-most spots and barrels go in behind them.
    /// </summary>
    internal static class CargoLoadService
    {
        private const string LoadStampName = "VC_HaulLoadStamp";
        // Items further than this from the boat aren't offered for loading.
        private const float MaxLoadDistance = 60f;

        internal static bool IsMarkedForLoad(ShipItem item)
        {
            return SupercargoTradeService.HasMarker(item, LoadStampName);
        }

        /// <summary>Whether the look prompt should offer loading (or cancelling loading) for this item.</summary>
        internal static bool CanOfferLoad(ShipItem item)
        {
            return IsMarkedForLoad(item) || CanQueueLoad(item, out _);
        }

        internal static bool CanQueueLoad(ShipItem item, out string reason)
        {
            reason = null;
            var manager = VirtualCrewManager.Instance;
            if (!item || manager == null)
                return false;

            // Bought (not a shop's display stock), loose on the dock, and not already on its way.
            if (!item.sold || item.held || item.nailed || item.currentActualBoat)
                return false;

            var body = item.GetItemRigidbody();
            if (!body || body.GetCurrentInventorySlot() != null || manager.HasPendingHaulLoadRequest(item))
                return false;

            var context = CrewBoatContextResolver.Resolve();
            if (context == null || !context.WorldBoat
                || Vector3.Distance(context.WorldBoat.position, item.transform.position) > MaxLoadDistance)
                return false;

            if (!manager.Crew.Any(c => c.Role == ShipRole.Deckhand))
            {
                reason = "No deckhands to load cargo.";
                return false;
            }

            if (!ShoreRoute.IsBoatHeld())
            {
                reason = "Moor or anchor the boat to load cargo.";
                return false;
            }

            var area = CargoAreaPainter.GetActiveArea();
            if (area == null || area.RunCount == 0)
            {
                reason = "Paint a cargo area to load cargo.";
                return false;
            }

            return true;
        }

        /// <summary>Queues the item for loading, or cancels loading it if it's already queued.</summary>
        internal static bool TryToggleLoad(ShipItem item)
        {
            var manager = VirtualCrewManager.Instance;
            if (!item || manager == null)
                return false;

            if (manager.TryCancelHaulLoadRequestForItem(item))
                return true;

            if (IsMarkedForLoad(item))
            {
                RemoveLoadStamp(item);
                return true;
            }

            if (!CanQueueLoad(item, out string reason))
            {
                if (reason != null)
                    NotificationUi.instance?.ShowNotification(reason);
                return false;
            }

            QueueLoad(item, null);
            return true;
        }

        internal static void RemoveLoadStamp(ShipItem item)
        {
            SupercargoTradeService.RemoveMarker(item, LoadStampName);
        }

        private static void QueueLoad(ShipItem item, System.Action<bool> onFirstPlan)
        {
            SupercargoTradeService.AttachMarker(item, LoadStampName, "LOAD", new Color(0.1f, 0.55f, 0.15f, 1f));
            VirtualCrewManager.Instance.AddHaulLoadRequest(new HaulLoadRequest(item, onFirstPlan));
        }

        // ---------------------------------------------------------------- loading everything

        private sealed class BatchEntry
        {
            public ShipItem Item;
            public int Prefab;
            public bool Round;
            public float Volume;
        }

        private sealed class LoadBatch
        {
            public readonly List<BatchEntry> Entries = new List<BatchEntry>();
            public readonly Dictionary<int, BatchEntry> ShapeByPrefab = new Dictionary<int, BatchEntry>();
            public int Classified;
            public bool Queued;
            public int Total;
            public int Resolved;
            public int NoRoom;
        }

        private static LoadBatch batch;

        internal static bool IsLoadingAll => batch != null;

        /// <summary>The player's cargo near the boat, not aboard, that could be loaded now.</summary>
        internal static List<ShipItem> FindLoadableDockCargo()
        {
            var items = new List<ShipItem>();
            if (!CanQueueAny())
                return items;

            foreach (var item in Object.FindObjectsOfType<ShipItem>())
                if (!IsMarkedForLoad(item) && CanQueueLoad(item, out _))
                    items.Add(item);
            return items;
        }

        // The checks that don't depend on the item, so a whole scan can be skipped when loading isn't possible.
        private static bool CanQueueAny()
        {
            var manager = VirtualCrewManager.Instance;
            var area = CargoAreaPainter.GetActiveArea();
            return manager != null
                && manager.Crew.Any(c => c.Role == ShipRole.Deckhand)
                && CrewRoleAvailability.HasAwakeCrew(ShipRole.Supercargo)
                && ShoreRoute.IsBoatHeld()
                && area != null && area.RunCount > 0;
        }

        /// <summary>Starts loading all of the player's cargo on the dock; the loads are queued over the next few frames.</summary>
        internal static int StartLoadingAll()
        {
            if (batch != null)
                return 0;

            var items = FindLoadableDockCargo();
            if (items.Count == 0)
                return 0;

            batch = new LoadBatch();
            foreach (var item in items)
                batch.Entries.Add(new BatchEntry { Item = item, Prefab = item.GetPrefabIndex() });
            NotificationUi.instance?.ShowNotification("Planning to load " + items.Count + " item" + (items.Count == 1 ? "" : "s") + "...");
            return items.Count;
        }

        /// <summary>Every frame: works out shapes for a pending batch, then queues its loads in order.</summary>
        internal static void Tick()
        {
            FrozenCargo.Tick();
            if (batch == null || batch.Queued)
                return;

            // Shapes: a new kind of item costs a measurement, so at most one of those a frame; kinds already seen are free.
            bool measured = false;
            while (batch.Classified < batch.Entries.Count)
            {
                var entry = batch.Entries[batch.Classified];
                if (entry.Prefab >= 0 && batch.ShapeByPrefab.TryGetValue(entry.Prefab, out var known))
                {
                    entry.Round = known.Round;
                    entry.Volume = known.Volume;
                }
                else
                {
                    if (measured)
                        return;
                    measured = true;
                    if (entry.Item && CargoPackingSolver.TryClassifyShape(entry.Item, out bool round, out float volume))
                    {
                        entry.Round = round;
                        entry.Volume = volume;
                    }
                    if (entry.Prefab >= 0)
                        batch.ShapeByPrefab[entry.Prefab] = entry;
                }

                batch.Classified++;
            }

            // Box-like first, then barrels; biggest first; like with like.
            var ordered = batch.Entries
                .Where(e => e.Item && CanQueueLoad(e.Item, out _))
                .OrderBy(e => e.Round)
                .ThenByDescending(e => e.Volume)
                .ThenBy(e => e.Prefab)
                .ToList();

            var current = batch;
            current.Queued = true;
            current.Total = ordered.Count;
            if (ordered.Count == 0)
            {
                batch = null;
                NotificationUi.instance?.ShowNotification("No cargo on the dock to load.");
                return;
            }

            foreach (var entry in ordered)
                QueueLoad(entry.Item, fits => OnBatchItemPlanned(current, fits));
        }

        private static void OnBatchItemPlanned(LoadBatch current, bool fits)
        {
            current.Resolved++;
            if (!fits)
                current.NoRoom++;
            if (current.Resolved < current.Total)
                return;

            if (batch == current)
                batch = null;

            int placed = current.Total - current.NoRoom;
            NotificationUi.instance?.ShowNotification(current.NoRoom == 0
                ? "Loading " + placed + " item" + (placed == 1 ? "" : "s") + "; all fit"
                : "Loading " + placed + " of " + current.Total + " items; " + current.NoRoom + " don't fit");
        }

        /// <summary>Forgets a batch in progress (a save was loaded); loads already queued carry on as usual.</summary>
        internal static void ClearBatch()
        {
            batch = null;
        }
    }
}
