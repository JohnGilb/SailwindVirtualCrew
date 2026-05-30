using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewLanternService
    {
        private const float DuskHour = 18f;
        private const float DawnHour = 6f;
        private const float RefillThreshold = 0.15f;

        private static float nextCheckGameHour = -1f;
        private static bool? lastWantedLit;

        internal static void Tick(IReadOnlyList<Crewman> crew)
        {
            if (crew == null || crew.Count == 0 || Sun.sun == null || GameState.currentBoat == null)
                return;

            float now = GameState.day * 24f + Sun.sun.globalTime;
            bool wantedLit = WantsLanternsLit(Sun.sun.localTime);
            if (lastWantedLit.HasValue && lastWantedLit.Value == wantedLit && now < nextCheckGameHour)
                return;

            lastWantedLit = wantedLit;
            nextCheckGameHour = now + (wantedLit ? 0.25f : 0.5f);
            ServiceCurrentBoatLanterns(wantedLit);
        }

        private static bool WantsLanternsLit(float localTime)
        {
            return localTime >= DuskHour || localTime < DawnHour;
        }

        private static void ServiceCurrentBoatLanterns(bool wantedLit)
        {
            foreach (ShipItemLight light in UnityEngine.Object.FindObjectsOfType<ShipItemLight>())
            {
                if (!light || !light.sold || !IsOnCurrentBoat(light))
                    continue;

                if (wantedLit)
                {
                    RefuelIfNeeded(light);
                    SetLight(light, true);
                }
                else
                {
                    SetLight(light, false);
                }
            }
        }

        private static void RefuelIfNeeded(ShipItemLight light)
        {
            float initialHealth = GetInitialHealth(light);
            if (initialHealth <= 0f || light.health / initialHealth > RefillThreshold)
                return;

            var usedFuel = new HashSet<ShipItemLanternFuel>();
            for (int i = 0; i < 8 && light.health < initialHealth * 0.95f; i++)
            {
                ShipItemLanternFuel fuel = FindMatchingFuel(light.usesOil, usedFuel);
                if (!fuel)
                    break;

                usedFuel.Add(fuel);
                float before = light.health;
                TryLoadFuel(light, fuel);

                if (fuel && light.health <= before + 0.001f)
                    ConsumeFuelDirectly(light, fuel, initialHealth);

                if (!light.usesOil)
                    break;
            }
        }

        private static ShipItemLanternFuel FindMatchingFuel(bool oil, HashSet<ShipItemLanternFuel> excluded)
        {
            foreach (ShipItemLanternFuel fuel in UnityEngine.Object.FindObjectsOfType<ShipItemLanternFuel>())
            {
                if (fuel
                    && fuel.sold
                    && fuel.oilBottle == oil
                    && (excluded == null || !excluded.Contains(fuel))
                    && (!oil || fuel.health > 0f)
                    && IsOnCurrentBoat(fuel))
                    return fuel;
            }

            return null;
        }

        private static void TryLoadFuel(ShipItemLight light, ShipItemLanternFuel fuel)
        {
            try
            {
                if (!fuel.oilBottle)
                    DetachFuelFromCrate(fuel);

                Traverse.Create(light).Method("LoadFuel", fuel).GetValue();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VirtualCrew] Crew lantern service failed to load lantern fuel: " + ex.Message);
            }
        }

        private static void ConsumeFuelDirectly(ShipItemLight light, ShipItemLanternFuel fuel, float initialHealth)
        {
            if (!fuel || fuel.oilBottle != light.usesOil)
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

        private static void DetachFuelFromCrate(ShipItemLanternFuel fuel)
        {
            CrateInventory crate = FindContainingCrate(fuel);
            if (!crate)
                return;

            crate.WithdrawItem(fuel);

            if (CrateInventoryUI.instance
                && CrateInventoryUI.instance.showingUI
                && CrateInventoryUI.instance.currentCrate == crate)
            {
                CrateInventoryUI.instance.RefreshButtons();
            }
        }

        private static bool IsOnCurrentBoat(ShipItem item)
        {
            Transform boat = GameState.currentBoat;
            if (!boat || !item)
                return false;

            if (item.currentActualBoat == boat || item.transform.IsChildOf(boat))
                return true;

            Transform root = GetCurrentBoatRoot();
            if (root && item.transform.IsChildOf(root))
                return true;

            if (IsInCrateOnCurrentBoat(item))
                return true;

            if (IsInCargoCarrierOnCurrentBoat(item))
                return true;

            return item.GetCurrentInventorySlot() >= 0;
        }

        private static Transform GetCurrentBoatRoot()
        {
            Transform boat = GameState.currentBoat;
            if (!boat) return null;
            var purchasableBoat = boat.GetComponentInParent<PurchasableBoat>();
            return purchasableBoat ? purchasableBoat.transform : boat;
        }

        private static bool IsInCrateOnCurrentBoat(ShipItem item)
        {
            CrateInventory crate = FindContainingCrate(item);
            if (!crate)
                return false;

            ShipItem crateItem = crate.GetComponent<ShipItem>();
            if (crateItem && IsDirectlyOnCurrentBoat(crateItem))
                return true;

            return false;
        }

        private static CrateInventory FindContainingCrate(ShipItem item)
        {
            foreach (CrateInventory crate in UnityEngine.Object.FindObjectsOfType<CrateInventory>())
            {
                if (crate && crate.containedItems != null && crate.containedItems.Contains(item))
                    return crate;
            }

            return null;
        }

        private static bool IsInCargoCarrierOnCurrentBoat(ShipItem item)
        {
            foreach (CargoCarrier carrier in UnityEngine.Object.FindObjectsOfType<CargoCarrier>())
            {
                if (!carrier || carrier.cargo == null || !carrier.cargo.Contains(item))
                    continue;

                ShipItem carrierItem = carrier.GetComponent<ShipItem>();
                if (carrierItem && IsDirectlyOnCurrentBoat(carrierItem))
                    return true;
            }

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
            return Traverse.Create(light).Field("initialHealth").GetValue<float>();
        }

        private static void SetLight(ShipItemLight light, bool state)
        {
            try
            {
                Traverse.Create(light).Method("SetLight", state).GetValue();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[VirtualCrew] Crew lantern service failed to set lantern state: " + ex.Message);
            }
        }
    }
}
