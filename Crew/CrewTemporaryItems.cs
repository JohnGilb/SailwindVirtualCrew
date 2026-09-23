using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    /// <summary>Marks a visual-only item copy made for a crewman to hold. See <see cref="CrewTemporaryItems"/>.</summary>
    internal sealed class CrewTemporaryItemMarker : MonoBehaviour
    {
    }

    /// <summary>
    /// Visual-only copies of game items for crew to hold: a lookout's spyglass, a swabber's broom, a steward's cup,
    /// a navigator's instrument. Each is a copy of the item's prefab with every script removed except the ShipItem
    /// itself, which stays (disabled) so the Player Model mod recognises the item and poses the hands for it.
    ///
    /// The copy is made under an inactive holder so nothing on it wakes up, stripped, and only then parented into
    /// the scene. ShipItem.Awake, which would register physics and save hooks, is skipped for marked copies (see
    /// TemporaryItemPatches). Copies have no SaveablePrefab, so they are never saved, and our own item scans skip
    /// them via <see cref="IsTemporary"/>.
    /// </summary>
    internal static class CrewTemporaryItems
    {
        private const string Phase = "CrewItems";
        private const string NamePrefix = "VC_TempItem_";

        private static GameObject _holder;

        internal static bool IsTemporary(Component component)
        {
            return component && component.GetComponent<CrewTemporaryItemMarker>() != null;
        }

        /// <summary>The prefab an item was spawned from, so a copy matches its type and quality exactly.</summary>
        internal static GameObject PrefabFor(ShipItem item)
        {
            if (!item)
                return null;

            var saveable = item.GetComponent<SaveablePrefab>();
            var directory = PrefabsDirectory.instance != null ? PrefabsDirectory.instance.directory : null;
            if (saveable == null || directory == null || saveable.prefabIndex < 0 || saveable.prefabIndex >= directory.Length)
                return null;

            return directory[saveable.prefabIndex];
        }

        /// <summary>The smallest cup-sized bottle prefab (the game calls capacity under 5 a mug).</summary>
        internal static GameObject FindCupPrefab()
        {
            GameObject best = null;
            float bestCapacity = float.MaxValue;
            foreach (var prefab in Directory())
            {
                var bottle = prefab ? prefab.GetComponent<ShipItemBottle>() : null;
                if (bottle == null)
                    continue;

                float capacity = bottle.GetCapacity();
                if (capacity < 5f && capacity < bestCapacity)
                {
                    best = prefab;
                    bestCapacity = capacity;
                }
            }
            return best;
        }

        internal static GameObject FindBroomPrefab()
        {
            foreach (var prefab in Directory())
            {
                if (prefab && prefab.GetComponent<ShipItemBroom>() != null)
                    return prefab;
            }
            return null;
        }

        /// <summary>
        /// Make a visual-only copy of <paramref name="prefab"/> under <paramref name="parent"/> at the given pose.
        /// Returns null if the copy could not be made.
        /// </summary>
        internal static GameObject Spawn(GameObject prefab, Transform parent, Vector3 position, Quaternion rotation, string label)
        {
            if (!prefab || !parent)
                return null;

            GameObject copy = null;
            try
            {
                copy = Object.Instantiate(prefab, GetHolder(), false);
                copy.name = NamePrefix + label;
                copy.AddComponent<CrewTemporaryItemMarker>();
                Strip(copy);

                // The holder sits at the origin with no rotation or scale, so the copy keeps the prefab's own
                // scale, which is the size the game spawns the item at.
                copy.transform.SetParent(parent, true);
                copy.transform.SetPositionAndRotation(position, rotation);
                return copy;
            }
            catch (System.Exception e)
            {
                CrewDebugLog.Warn(Phase, "Could not create temporary '" + label + "' from '" + prefab.name + "': " + e.Message);
                if (copy)
                    Object.Destroy(copy);
                return null;
            }
        }

        internal static void Destroy(GameObject copy)
        {
            if (copy)
                Object.Destroy(copy);
        }

        private static GameObject[] Directory()
        {
            var directory = PrefabsDirectory.instance != null ? PrefabsDirectory.instance.directory : null;
            return directory ?? new GameObject[0];
        }

        private static Transform GetHolder()
        {
            if (!_holder)
            {
                _holder = new GameObject("VC_TempItemHolder");
                _holder.SetActive(false);
            }
            return _holder.transform;
        }

        // Runs while the copy is still inactive: nothing here has woken up yet.
        private static void Strip(GameObject copy)
        {
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour is CrewTemporaryItemMarker)
                    continue;

                // The item class is what the Player Model mod reads to pick a grip. The broom's Cleaner is kept for
                // its sweeping frame, which the hands follow. Both stay disabled, so they never update.
                if (behaviour is ShipItem || behaviour is Cleaner)
                {
                    behaviour.enabled = false;
                    continue;
                }

                behaviour.enabled = false;
                Object.DestroyImmediate(behaviour);
            }

            // ShipItem requires a collider and a rigidbody, so they are switched off rather than removed.
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.useGravity = false;
                body.detectCollisions = false;
            }

            // A spyglass carries its own zoom camera.
            foreach (var camera in copy.GetComponentsInChildren<Camera>(true))
                camera.enabled = false;

            foreach (var listener in copy.GetComponentsInChildren<AudioListener>(true))
                Object.DestroyImmediate(listener);

            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true))
            {
                audio.playOnAwake = false;
                audio.enabled = false;
            }

            foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                main.playOnAwake = false;
                var emission = particles.emission;
                emission.enabled = false;
            }

            var broom = copy.GetComponent<ShipItemBroom>();
            if (broom != null)
                ShowHeldBroom(broom);
        }

        // The Cleaner shows the bending skinned broom while held and a static one otherwise. It is disabled on the
        // copy, so pick the held look once.
        private static void ShowHeldBroom(ShipItemBroom broom)
        {
            var cleaner = Traverse.Create(broom).Field("cleaner").GetValue<Cleaner>();
            if (cleaner == null)
                return;

            var traverse = Traverse.Create(cleaner);
            var staticBroom = traverse.Field("staticBroom").GetValue<Renderer>();
            var skinnedBroom = traverse.Field("skinnedBroom").GetValue<Renderer>();
            if (staticBroom != null && skinnedBroom != null)
            {
                staticBroom.enabled = false;
                skinnedBroom.enabled = true;
            }
        }
    }

    /// <summary>A temporary item a crewman is holding, bound to the task it was made for.</summary>
    internal sealed class CrewHeldProp
    {
        private const float SweepFlipSeconds = 0.55f;
        private const float SweepLerpRate = 5f;
        private const float SweepMinSpeed = 0.25f;

        private readonly Transform _sweepFrame;
        private readonly float _sweepRotation;
        private readonly float _sweepOffset;
        private float _sweepTarget;
        private float _nextSweepFlip;

        internal GameObject Object { get; }
        internal object Task { get; }
        internal Transform Transform => Object ? Object.transform : null;

        private CrewHeldProp(GameObject copy, object task)
        {
            Object = copy;
            Task = task;

            var broom = copy.GetComponent<ShipItemBroom>();
            var cleaner = broom != null ? Traverse.Create(broom).Field("cleaner").GetValue<Cleaner>() : null;
            if (cleaner != null)
            {
                _sweepFrame = cleaner.transform.parent;
                _sweepRotation = Traverse.Create(cleaner).Field("sidesweepRot").GetValue<float>();
                _sweepOffset = Traverse.Create(cleaner).Field("sidesweepMov").GetValue<float>();
            }
        }

        internal static CrewHeldProp Create(GameObject prefab, object task, Transform parent, Vector3 position, Quaternion rotation, string label)
        {
            var copy = CrewTemporaryItems.Spawn(prefab, parent, position, rotation, label);
            return copy ? new CrewHeldProp(copy, task) : null;
        }

        /// <summary>
        /// A broom sweeps side to side while its holder walks, the way the game's Cleaner sweeps on each click, and
        /// settles back to centre when they stop. The Player Model mod moves the hands with the sweeping frame.
        /// </summary>
        internal void TickSweep(float speedMps, float deltaTime)
        {
            if (_sweepFrame == null)
                return;

            if (speedMps < SweepMinSpeed)
            {
                _sweepTarget = 0f;
            }
            else if (Time.time >= _nextSweepFlip)
            {
                _sweepTarget = _sweepTarget > 0f ? -1f : 1f;
                _nextSweepFlip = Time.time + SweepFlipSeconds;
            }

            float t = deltaTime * SweepLerpRate;
            _sweepFrame.localRotation = Quaternion.Slerp(_sweepFrame.localRotation, Quaternion.Euler(0f, 0f, _sweepTarget * _sweepRotation), t);
            _sweepFrame.localPosition = Vector3.Lerp(_sweepFrame.localPosition, new Vector3(_sweepTarget * _sweepOffset, 0f, 0f), t);
        }

        internal void Destroy()
        {
            CrewTemporaryItems.Destroy(Object);
        }
    }
}
