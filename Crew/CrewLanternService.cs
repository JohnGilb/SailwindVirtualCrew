using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewLanternService
    {
        internal const float RefillThreshold = 0.15f;
        private const float ServiceSearchRadius = 4f;
        private const float PreferredStandDistance = 1.2f;

        internal static IEnumerable<ShipItemLight> FindCurrentVesselLanterns()
        {
            foreach (var light in UnityEngine.Object.FindObjectsOfType<ShipItemLight>())
                if (IsServiceableLanternTarget(light))
                    yield return light;
        }

        internal static bool IsLanternLit(ShipItemLight light)
        {
            return light && light.amount >= 0.5f && light.health > 0f;
        }

        internal static bool NeedsRefill(ShipItemLight light)
        {
            float initialHealth = GetInitialHealth(light);
            return light && initialHealth > 0f && light.health / initialHealth <= RefillThreshold;
        }

        internal static bool CanAcceptFuel(ShipItemLight light, ShipItemLanternFuel fuel)
        {
            return light && fuel && fuel.sold && fuel.oilBottle == light.usesOil
                && (!fuel.oilBottle || fuel.health > 0f)
                && IsOnCurrentBoat(fuel);
        }

        internal static ShipItemLanternFuel FindFuelFor(ShipItemLight light, IEnumerable<ShipItemLanternFuel> excluded = null)
        {
            if (!light)
                return null;

            var excludedSet = excluded != null
                ? new HashSet<ShipItemLanternFuel>(excluded.Where(f => f != null))
                : null;

            foreach (var fuel in UnityEngine.Object.FindObjectsOfType<ShipItemLanternFuel>())
            {
                if (!CanAcceptFuel(light, fuel))
                    continue;
                if (excludedSet != null && excludedSet.Contains(fuel))
                    continue;
                return fuel;
            }

            return null;
        }

        internal static bool TryGetLanternServicePose(ShipItemLight light, out Vector3 localPosition, out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            var boat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (!light || !boat)
                return false;

            Vector3 lanternLocal = boat.InverseTransformPoint(light.transform.position);
            Vector3 outward = lanternLocal;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f)
                outward = Vector3.back;
            outward.Normalize();

            Vector3[] candidates =
            {
                lanternLocal - outward * PreferredStandDistance,
                lanternLocal + outward * PreferredStandDistance,
                lanternLocal + Vector3.right * PreferredStandDistance,
                lanternLocal - Vector3.right * PreferredStandDistance,
                lanternLocal + Vector3.forward * PreferredStandDistance,
                lanternLocal - Vector3.forward * PreferredStandDistance,
                lanternLocal
            };

            foreach (var candidate in candidates)
            {
                if (!CrewNavigationCoordinator.Instance.TryProjectLocalToNavMeshQuiet(candidate, ServiceSearchRadius, out localPosition))
                    continue;

                Vector3 look = lanternLocal - localPosition;
                look.y = 0f;
                localRotation = look.sqrMagnitude >= 0.001f
                    ? Quaternion.LookRotation(look.normalized, Vector3.up)
                    : Quaternion.identity;
                return true;
            }

            localPosition = lanternLocal;
            localRotation = Quaternion.identity;
            return false;
        }

        internal static float EstimateDistanceToLantern(Crewman crewman, ShipItemLight light)
        {
            if (!TryGetLanternServicePose(light, out var localPosition, out _))
                return float.MaxValue;

            return CrewNavigationCoordinator.Instance.EstimateDistanceToLocalPosition(crewman, localPosition, ServiceSearchRadius);
        }

        internal static void SetLight(ShipItemLight light, bool state)
        {
            if (!light)
                return;

            try
            {
                Traverse.Create(light).Method("SetLight", state).GetValue();
                light.UpdateLookText();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VirtualCrew] Failed to set lantern state: " + ex.Message);
            }
        }

        internal static void LoadFuel(ShipItemLight light, ShipItemLanternFuel fuel)
        {
            if (!CanAcceptFuel(light, fuel))
                return;

            try
            {
                if (!fuel.oilBottle)
                    DetachFuelFromCrate(fuel);

                float before = light.health;
                Traverse.Create(light).Method("LoadFuel", fuel).GetValue();
                if (fuel && light.health <= before + 0.001f)
                    ConsumeFuelDirectly(light, fuel);
                light.UpdateLookText();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VirtualCrew] Failed to load lantern fuel: " + ex.Message);
            }
        }

        internal static bool IsOnCurrentBoat(ShipItem item)
        {
            Transform boat = GameState.currentBoat;
            if (!boat || !item)
                return false;

            if (item.currentActualBoat == boat || item.transform.IsChildOf(boat))
                return true;

            Transform root = GetCurrentBoatRoot();
            if (root && item.transform.IsChildOf(root))
                return true;

            return IsInCrateOnCurrentBoat(item)
                || IsInCargoCarrierOnCurrentBoat(item)
                || item.GetCurrentInventorySlot() >= 0;
        }

        internal static bool IsServiceableLanternTarget(ShipItemLight light)
        {
            return light
                && light.sold
                && IsDirectlyOnCurrentBoat(light)
                && !FindContainingCrate(light)
                && !IsInCargoCarrier(light)
                && light.GetCurrentInventorySlot() < 0;
        }

        internal static CrateInventory FindContainingCrate(ShipItem item)
        {
            foreach (var crate in UnityEngine.Object.FindObjectsOfType<CrateInventory>())
                if (crate && crate.containedItems != null && crate.containedItems.Contains(item))
                    return crate;

            return null;
        }

        private static void ConsumeFuelDirectly(ShipItemLight light, ShipItemLanternFuel fuel)
        {
            if (!CanAcceptFuel(light, fuel))
                return;

            float initialHealth = GetInitialHealth(light);
            if (initialHealth <= 0f)
                return;

            if (light.usesOil)
            {
                float added = fuel.RequestOil(Mathf.Abs(light.health - initialHealth));
                light.health += added;
            }
            else
            {
                DetachFuelFromCrate(fuel);
                fuel.RequestCandle();
                light.health = initialHealth;
            }
        }

        internal static void DetachFuelFromCrate(ShipItemLanternFuel fuel)
        {
            var crate = FindContainingCrate(fuel);
            if (!crate)
                return;

            crate.WithdrawItem(fuel);
            if (CrateInventoryUI.instance
                && CrateInventoryUI.instance.showingUI
                && CrateInventoryUI.instance.currentCrate == crate)
                CrateInventoryUI.instance.RefreshButtons();
        }

        private static Transform GetCurrentBoatRoot()
        {
            Transform boat = GameState.currentBoat;
            if (!boat)
                return null;

            var purchasableBoat = boat.GetComponentInParent<PurchasableBoat>();
            return purchasableBoat ? purchasableBoat.transform : boat;
        }

        private static bool IsInCrateOnCurrentBoat(ShipItem item)
        {
            var crate = FindContainingCrate(item);
            if (!crate)
                return false;

            var crateItem = crate.GetComponent<ShipItem>();
            return crateItem && IsDirectlyOnCurrentBoat(crateItem);
        }

        private static bool IsInCargoCarrierOnCurrentBoat(ShipItem item)
        {
            foreach (var carrier in UnityEngine.Object.FindObjectsOfType<CargoCarrier>())
            {
                if (!carrier || carrier.cargo == null || !carrier.cargo.Contains(item))
                    continue;

                var carrierItem = carrier.GetComponent<ShipItem>();
                if (carrierItem && IsDirectlyOnCurrentBoat(carrierItem))
                    return true;
            }

            return false;
        }

        private static bool IsInCargoCarrier(ShipItem item)
        {
            foreach (var carrier in UnityEngine.Object.FindObjectsOfType<CargoCarrier>())
                if (carrier && carrier.cargo != null && carrier.cargo.Contains(item))
                    return true;

            return false;
        }

        private static bool IsDirectlyOnCurrentBoat(ShipItem item)
        {
            Transform boat = GameState.currentBoat;
            Transform root = GetCurrentBoatRoot();
            return item
                && ((boat && (item.currentActualBoat == boat || item.transform.IsChildOf(boat)))
                    || (root && item.transform.IsChildOf(root)));
        }

        private static float GetInitialHealth(ShipItemLight light)
        {
            if (!light)
                return 0f;

            try
            {
                return Traverse.Create(light).Field("initialHealth").GetValue<float>();
            }
            catch
            {
                return 0f;
            }
        }
    }
}
