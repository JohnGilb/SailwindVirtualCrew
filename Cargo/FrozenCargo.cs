using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// Cargo that deckhands have stowed while loading is still under way. The planner may put an item on top of a spot
    /// that's only reserved, and deliveries don't always land in plan order, so an item can be set down before the one
    /// meant to hold it up. Stowed items are therefore frozen where they're put: solid (they block, and hold up, what
    /// comes after), but out of the physics simulation, so an early one hangs in place instead of falling. When loading
    /// is finished (no load requests left), everything is released to physics at once, and anything still hanging
    /// settles onto what is now beneath it. An item the player picks up is released at once.
    ///
    /// While frozen, nothing keeps the visible item on its physics body (the game's ItemRigidbody, which normally does,
    /// is paused), so each frozen item is pinned at the boat-local pose it was stowed at: anything that moves it (a crew
    /// body letting go of it a frame late) is undone.
    /// </summary>
    internal static class FrozenCargo
    {
        private sealed class Entry
        {
            public ShipItem Item;
            public CarriedCargo Cargo;
            // Where it was stowed, relative to the boat it's parented to.
            public Transform Parent;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private static readonly List<Entry> Frozen = new List<Entry>();

        internal static int Count => Frozen.Count;

        internal static void Add(ShipItem item, CarriedCargo cargo)
        {
            if (item && cargo != null && cargo.IsFrozen)
                Frozen.Add(new Entry
                {
                    Item = item,
                    Cargo = cargo,
                    Parent = item.transform.parent,
                    LocalPosition = item.transform.localPosition,
                    LocalRotation = item.transform.localRotation
                });
        }

        /// <summary>Every frame: releases items the player picks up, and everything once loading is done.</summary>
        internal static void Tick()
        {
            if (Frozen.Count == 0)
                return;

            for (int i = Frozen.Count - 1; i >= 0; i--)
            {
                var entry = Frozen[i];
                if (!entry.Item)
                {
                    Frozen.RemoveAt(i);
                    continue;
                }

                if (entry.Item.held)
                {
                    entry.Cargo.Unfreeze();
                    Frozen.RemoveAt(i);
                    continue;
                }

                var transform = entry.Item.transform;
                if (transform.parent == entry.Parent
                    && (transform.localPosition != entry.LocalPosition || transform.localRotation != entry.LocalRotation))
                {
                    transform.localPosition = entry.LocalPosition;
                    transform.localRotation = entry.LocalRotation;
                }
            }

            if (Frozen.Count > 0 && !IsLoadingUnderway())
                ReleaseAll();
        }

        private static bool IsLoadingUnderway()
        {
            var manager = VirtualCrewManager.Instance;
            return CargoLoadService.IsLoadingAll
                || (manager != null && manager.HaulLoadRequests != null
                    && manager.HaulLoadRequests.Any(r => r.Status != WorkRequestStatus.Complete));
        }

        internal static void ReleaseAll()
        {
            int count = 0;
            foreach (var entry in Frozen)
            {
                if (!entry.Item)
                    continue;
                entry.Cargo.Unfreeze();
                count++;
            }

            Frozen.Clear();
            if (count > 0)
            {
                CrewDebugLog.Ok("CargoLoad", "Loading finished: released " + count + " stowed item" + (count == 1 ? "" : "s") + " to physics.");
                NotificationUi.instance?.ShowNotification("Cargo loading finished");
            }
        }

        /// <summary>Forgets frozen items without touching them (a save was loaded; they're gone or fresh).</summary>
        internal static void Clear()
        {
            Frozen.Clear();
        }
    }
}
