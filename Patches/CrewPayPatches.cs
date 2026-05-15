using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class CrewPayPatchState
    {
        internal static PendingCargoPurchase PendingEconomyPurchase;
        internal static PendingCargoSale PendingEconomySale;

        internal static bool IsCargo(ShipItem item)
        {
            return item != null && item.GetComponent<Good>() != null;
        }

        internal static int GetShopkeeperCurrency(Shopkeeper shopkeeper)
        {
            var region = Traverse.Create(shopkeeper).Field("parentRegion").GetValue<Region>();
            return region != null ? (int)region.portRegion : -1;
        }

        internal static int GetEconomyBuyPrice(IslandMarket island, int goodIndex, Currency currency)
        {
            int rawPrice = island.knownPrices[island.GetPortIndex()].buyPrices[goodIndex];
            bool withConversionFee = island.GetPort().region != (PortRegion)currency;
            return CurrencyMarket.instance.GetBuyPriceInCurrency(currency, rawPrice, withConversionFee);
        }

        internal static int GetEconomySellPrice(IslandMarket island, int goodIndex, Currency currency)
        {
            int rawPrice = island.knownPrices[island.GetPortIndex()].sellPrices[goodIndex];
            bool withConversionFee = island.GetPort().region != (PortRegion)currency;
            return CurrencyMarket.instance.GetSellPriceInCurrency(currency, rawPrice, withConversionFee);
        }

        internal static Good FindWarehouseGood(IslandMarket island, int goodIndex)
        {
            var warehouse = island != null ? island.GetWarehouseArea() : null;
            if (warehouse == null)
                return null;

            var goods = Traverse.Create(warehouse).Field("goodsInArea").GetValue<List<Good>>();
            if (goods == null)
                return null;

            foreach (var good in goods)
            {
                if (good == null || !IsWarehouseGoodValid(good))
                    continue;

                var saveable = good.GetComponent<SaveablePrefab>();
                if (saveable != null && PrefabsDirectory.ItemToGoodIndex(saveable.prefabIndex) == goodIndex)
                    return good;
            }

            return null;
        }

        private static bool IsWarehouseGoodValid(Good good)
        {
            var saveable = good.GetComponent<SaveablePrefab>();
            if (saveable == null)
                return false;

            var prefab = PrefabsDirectory.instance.directory[saveable.prefabIndex];
            var crate = good.GetComponent<ShipItemCrate>();
            if (crate != null)
            {
                float amount = prefab.GetComponent<ShipItemCrate>().amount;
                if (crate.amount < amount)
                    return false;
            }

            var bottle = good.GetComponent<ShipItemBottle>();
            if (bottle != null)
            {
                float health = prefab.GetComponent<ShipItemBottle>().health;
                if (bottle.health < health)
                    return false;
            }

            var salt = good.GetComponent<ShipItemSalt>();
            if (salt != null)
            {
                float amount = prefab.GetComponent<ShipItemSalt>().amount;
                if (salt.amount < amount)
                    return false;
            }

            return true;
        }
    }

    internal sealed class PendingCargoPurchase
    {
        internal int PrefabIndex;
        internal int Price;
        internal int Currency;
    }

    internal sealed class PendingCargoSale
    {
        internal ShipItem Item;
        internal int Price;
        internal int Currency;
    }

    [HarmonyPatch(typeof(Shopkeeper), "SellItem")]
    internal static class ShopkeeperSellItemCrewPayPatch
    {
        private static void Postfix(ShipItem item, int price, int currency)
        {
            if (!CrewPayPatchState.IsCargo(item))
                return;

            VirtualCrewManager.Instance.RecordCargoPurchase(item, price, currency);
        }
    }

    [HarmonyPatch(typeof(Shopkeeper), "BuyItem")]
    internal static class ShopkeeperBuyItemCrewPayPatch
    {
        private static void Postfix(Shopkeeper __instance, ShipItem item, int price)
        {
            if (!CrewPayPatchState.IsCargo(item))
                return;

            int currency = CrewPayPatchState.GetShopkeeperCurrency(__instance);
            VirtualCrewManager.Instance.RecordCargoSale(item, price, currency);
        }
    }

    [HarmonyPatch(typeof(EconomyUI), "BuyGood")]
    internal static class EconomyUiBuyGoodCrewPayPatch
    {
        private static void Prefix(EconomyUI __instance)
        {
            CrewPayPatchState.PendingEconomyPurchase = null;

            var island = Traverse.Create(__instance).Field("currentIsland").GetValue<IslandMarket>();
            var currency = Traverse.Create(__instance).Field("currentPlayerCurrency").GetValue<Currency>();
            int goodIndex = Traverse.Create(__instance).Field("currentSelectedGood").GetValue<int>();
            if (island == null || !island.HasGood(goodIndex))
                return;
            if (currency != (Currency)island.GetPortRegion() && !island.allowCurrencyConversion)
                return;

            int price = CrewPayPatchState.GetEconomyBuyPrice(island, goodIndex, currency);
            if (PlayerGold.currency == null || PlayerGold.currency.Length <= (int)currency || PlayerGold.currency[(int)currency] < price)
                return;

            CrewPayPatchState.PendingEconomyPurchase = new PendingCargoPurchase
            {
                PrefabIndex = PrefabsDirectory.GoodToItemIndex(goodIndex),
                Price = price,
                Currency = (int)currency
            };
        }

        private static void Postfix()
        {
            CrewPayPatchState.PendingEconomyPurchase = null;
        }
    }

    [HarmonyPatch(typeof(SaveablePrefab), "RegisterToSave")]
    internal static class SaveablePrefabRegisterCrewPayPatch
    {
        private static void Postfix(SaveablePrefab __instance)
        {
            var pending = CrewPayPatchState.PendingEconomyPurchase;
            if (pending == null || __instance == null || __instance.prefabIndex != pending.PrefabIndex)
                return;

            var item = __instance.GetComponent<ShipItem>();
            if (!CrewPayPatchState.IsCargo(item))
                return;

            VirtualCrewManager.Instance.RecordCargoPurchase(item, pending.Price, pending.Currency);
            CrewPayPatchState.PendingEconomyPurchase = null;
        }
    }

    [HarmonyPatch(typeof(EconomyUI), "SellGood")]
    internal static class EconomyUiSellGoodCrewPayPatch
    {
        private static void Prefix(EconomyUI __instance)
        {
            CrewPayPatchState.PendingEconomySale = null;

            var island = Traverse.Create(__instance).Field("currentIsland").GetValue<IslandMarket>();
            var currency = Traverse.Create(__instance).Field("currentPlayerCurrency").GetValue<Currency>();
            int goodIndex = Traverse.Create(__instance).Field("currentSelectedGood").GetValue<int>();
            if (island == null || island.currentPlayerGoods[goodIndex] <= 0)
                return;
            if (currency != (Currency)island.GetPortRegion() && !island.allowCurrencyConversion)
                return;

            Good good = CrewPayPatchState.FindWarehouseGood(island, goodIndex);
            if (good == null)
                return;

            CrewPayPatchState.PendingEconomySale = new PendingCargoSale
            {
                Item = good.GetComponent<ShipItem>(),
                Price = CrewPayPatchState.GetEconomySellPrice(island, goodIndex, currency),
                Currency = (int)currency
            };
        }

        private static void Postfix()
        {
            var pending = CrewPayPatchState.PendingEconomySale;
            if (pending != null)
                VirtualCrewManager.Instance.RecordCargoSale(pending.Item, pending.Price, pending.Currency);
            CrewPayPatchState.PendingEconomySale = null;
        }
    }
}
