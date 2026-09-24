using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>
    /// A cargo item while a crewman carries it: its colliders, physics body and item rigidbody are switched off so it
    /// can be moved by hand (kinematically) through anything, and switched back on exactly as they were when it is set
    /// down; or set down frozen (solid, but held in place out of the simulation) and unfrozen later. Also puts an item
    /// straight aboard a boat, as the game's embark trigger would.
    /// </summary>
    internal sealed class CarriedCargo
    {
        private static readonly MethodInfo EnterBoatMethod = AccessTools.Method(typeof(ShipItem), "EnterBoat");
        private static readonly FieldInfo StayedEmbarkColField = AccessTools.Field(typeof(ShipItem), "currentlyStayedEmbarkCol");

        private readonly ShipItem item;
        private Collider itemCollider;
        private bool itemColliderWasEnabled;
        private ItemRigidbody itemRigidbody;
        private bool disableColWasSet;
        private bool itemRigidbodyWasEnabled;
        private Rigidbody body;
        private bool bodyWasKinematic;
        private bool bodyDetectedCollisions;
        private Collider[] bodyColliders;
        private bool[] bodyColliderStates;
        private Collider[] childColliders;
        private bool[] childColliderStates;

        internal CarriedCargo(ShipItem item)
        {
            this.item = item;
        }

        internal bool IsSuspended { get; private set; }
        internal bool IsFrozen { get; private set; }

        internal void Suspend()
        {
            if (IsSuspended || !item)
                return;

            itemCollider = item.GetComponent<Collider>();
            if (itemCollider != null)
                itemColliderWasEnabled = itemCollider.enabled;

            childColliders = item.GetComponentsInChildren<Collider>(true);
            childColliderStates = new bool[childColliders.Length];
            for (int i = 0; i < childColliders.Length; i++)
            {
                if (childColliders[i] == null)
                    continue;
                childColliderStates[i] = childColliders[i].enabled;
                childColliders[i].enabled = false;
            }

            itemRigidbody = item.GetItemRigidbody();
            if (itemRigidbody != null)
            {
                itemRigidbodyWasEnabled = itemRigidbody.enabled;
                disableColWasSet = itemRigidbody.disableCol;
                itemRigidbody.disableCol = true;
                itemRigidbody.ToggleCollider(false);

                body = itemRigidbody.GetBody();
                if (body != null)
                {
                    bodyWasKinematic = body.isKinematic;
                    bodyDetectedCollisions = body.detectCollisions;
                    body.isKinematic = true;
                    body.detectCollisions = false;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                bodyColliders = itemRigidbody.GetComponentsInChildren<Collider>(true);
                bodyColliderStates = new bool[bodyColliders.Length];
                for (int i = 0; i < bodyColliders.Length; i++)
                {
                    if (bodyColliders[i] == null)
                        continue;
                    bodyColliderStates[i] = bodyColliders[i].enabled;
                    bodyColliders[i].enabled = false;
                }

                itemRigidbody.enabled = false;
            }

            IsSuspended = true;
        }

        /// <summary>Moves the carried item (and its physics body, wherever that lives) to a world pose.</summary>
        internal void MoveTo(Vector3 position, Quaternion rotation)
        {
            if (!item)
                return;

            item.transform.position = position;
            item.transform.rotation = rotation;
            SyncBody(position, rotation);
        }

        /// <summary>
        /// Switches the item's colliders back on. Frozen, its body stays kinematic and its item rigidbody paused, so it
        /// is solid but stays exactly where it was put until <see cref="Unfreeze"/>.
        /// </summary>
        internal void Restore(bool frozen = false)
        {
            if (!IsSuspended)
                return;
            IsSuspended = false;
            if (!item)
                return;

            if (itemCollider != null)
                itemCollider.enabled = itemColliderWasEnabled;

            if (itemRigidbody != null)
            {
                SyncBody(item.transform.position, item.transform.rotation);
                if (bodyColliders != null)
                    for (int i = 0; i < bodyColliders.Length && i < bodyColliderStates.Length; i++)
                        if (bodyColliders[i] != null)
                            bodyColliders[i].enabled = bodyColliderStates[i];

                if (body != null)
                {
                    body.isKinematic = frozen || bodyWasKinematic;
                    body.detectCollisions = bodyDetectedCollisions;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                itemRigidbody.disableCol = disableColWasSet;
                // Left off while frozen: it would otherwise hand the body back to the simulation on its next step.
                itemRigidbody.enabled = !frozen && itemRigidbodyWasEnabled;
                itemRigidbody.ToggleCollider(!itemRigidbody.disableCol);
            }

            IsFrozen = frozen;

            if (childColliders != null)
                for (int i = 0; i < childColliders.Length && i < childColliderStates.Length; i++)
                    if (childColliders[i] != null)
                        childColliders[i].enabled = childColliderStates[i];

            childColliders = null;
            childColliderStates = null;
            bodyColliders = null;
            bodyColliderStates = null;
        }

        /// <summary>Hands a frozen item back to the physics simulation.</summary>
        internal void Unfreeze()
        {
            if (!IsFrozen)
                return;
            IsFrozen = false;
            if (!item || itemRigidbody == null)
                return;

            if (body != null)
            {
                body.isKinematic = bodyWasKinematic;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            itemRigidbody.enabled = itemRigidbodyWasEnabled;
        }

        /// <summary>
        /// Puts the item aboard <paramref name="worldBoat"/> now, as the game does when it sits in the boat's embark
        /// trigger: parented to the boat, its physics body moved into the walk collider, counted in the boat's mass.
        /// </summary>
        internal static bool EnterBoat(ShipItem item, Transform worldBoat)
        {
            if (!item || !worldBoat || EnterBoatMethod == null)
                return false;

            Collider embarkCol = null;
            foreach (var embark in worldBoat.GetComponentsInChildren<BoatEmbarkCollider>(true))
            {
                var collider = embark.GetComponent<Collider>();
                if (collider && collider.CompareTag("EmbarkCol") && embark.transform.parent == worldBoat)
                {
                    embarkCol = collider;
                    break;
                }
            }

            if (!embarkCol)
                return false;

            EnterBoatMethod.Invoke(item, new object[] { embarkCol });
            // Without this the item would leave again on its next physics step, before its trigger reports it inside.
            StayedEmbarkColField?.SetValue(item, embarkCol);
            item.frameCounter = 0;
            return true;
        }

        private void SyncBody(Vector3 position, Quaternion rotation)
        {
            if (body == null || !item)
                return;

            // Aboard, the physics body lives in the walk collider: at the same boat-local pose.
            if (item.currentActualBoat && item.currentWalkCol)
            {
                Vector3 local = item.currentActualBoat.InverseTransformPoint(position);
                Quaternion localRotation = Quaternion.Inverse(item.currentActualBoat.rotation) * rotation;
                body.transform.position = item.currentWalkCol.TransformPoint(local);
                body.transform.rotation = item.currentWalkCol.rotation * localRotation;
            }
            else
            {
                body.transform.position = position;
                body.transform.rotation = rotation;
            }

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}
