using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class SupercargoTradeService
    {
        private const float PortDudeRange = 100f;
        private const string StampName = "VC_HaulSellStamp";

        internal static bool CanOfferSellAtPort(ShipItem item)
        {
            return HasStamp(item) || TryGetSellContext(item, out _, out _);
        }

        internal static bool IsMarkedForHaulSell(ShipItem item)
        {
            return HasStamp(item);
        }

        internal static bool TryToggleSellAtPort(ShipItem item)
        {
            if (!item)
                return false;

            var manager = VirtualCrewManager.Instance;
            if (manager != null && manager.TryCancelHaulSellRequestForItem(item))
                return true;

            if (HasStamp(item))
            {
                RemoveStamp(item);
                return true;
            }

            return TryQueueSellAtPort(item);
        }

        internal static bool TryQueueSellAtPort(ShipItem item)
        {
            if (!TryGetSellContext(item, out var portDude, out int goodIndex))
                return false;

            var manager = VirtualCrewManager.Instance;
            if (manager == null || manager.HasPendingHaulSellRequest(item))
                return false;

            AttachStamp(item);
            manager.AddHaulSellRequest(new HaulSellRequest(item, portDude, goodIndex));
            return true;
        }

        internal static bool TryGetSellContext(ShipItem item, out PortDude portDude, out int goodIndex)
        {
            portDude = null;
            goodIndex = -1;

            if (!IsEligibleCargo(item))
                return false;

            var manager = VirtualCrewManager.Instance;
            if (manager == null)
                return false;

            if (manager.HasPendingHaulSellRequest(item))
                return false;

            if (!CrewRoleAvailability.HasAwakeCrew(ShipRole.Supercargo))
                return false;

            if (!manager.Crew.Any(c => c.Role == ShipRole.Deckhand))
                return false;

            if (!MooringLocator.IsCurrentBoatMoored())
                return false;

            if (!TryFindNearestPortDude(out portDude))
                return false;

            var market = portDude.GetPort()?.GetComponent<IslandMarket>();
            if (!CanSellCargoAtMarket(item, market))
                return false;

            var saveable = item.GetComponent<SaveablePrefab>();
            if (!saveable)
                return false;

            goodIndex = PrefabsDirectory.ItemToGoodIndex(saveable.prefabIndex);
            return goodIndex > 0;
        }

        internal static bool TryFindNearestPortDude(out PortDude nearest)
        {
            nearest = null;
            if (!CrewBoatContextResolver.TryResolveBoatTransforms(out var topBoat, out var worldBoat) || Port.ports == null)
                return false;

            Vector3 boatPosition = topBoat ? topBoat.position : worldBoat.position;
            float bestDistance = float.MaxValue;
            foreach (var port in Port.ports)
            {
                if (port == null)
                    continue;

                var dude = port.GetDude();
                if (!dude)
                    continue;

                float distance = Vector3.Distance(boatPosition, dude.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = dude;
                }
            }

            return nearest != null && bestDistance <= PortDudeRange;
        }

        internal static bool TrySellCargoAtPort(ShipItem item, IslandMarket market, int goodIndex)
        {
            if (!item || goodIndex <= 0 || !CanSellCargoAtMarket(item, market))
            {
                NotificationUi.instance?.ShowNotification("Failed to sell - crate/barrel not full");
                return false;
            }

            Port port = market.GetPort();
            if (port == null)
                return false;

            int currency = GameState.currentCurrency;
            if (PlayerGold.currency == null || currency < 0 || currency >= PlayerGold.currency.Length)
                return false;

            market.UpdateSelfPriceReportForPlayer();
            int sellPrice = GetSellPrice(market, goodIndex, currency);
            market.SellGood(goodIndex);
            if (market.currentPlayerGoods != null
                && goodIndex < market.currentPlayerGoods.Length
                && market.currentPlayerGoods[goodIndex] > 0)
                market.currentPlayerGoods[goodIndex]--;

            PlayerGold.currency[currency] += sellPrice;
            LogTransaction(item, goodIndex, sellPrice, currency, market.GetPortName());
            RemoveStamp(item);
            item.DestroyItem();
            UISoundPlayer.instance?.PlayGoldSound();
            return true;
        }

        internal static void AttachStamp(ShipItem item)
        {
            if (!item || item.transform.Find(StampName) != null)
                return;

            var stamp = new GameObject(StampName);
            stamp.transform.SetParent(item.transform, false);
            stamp.transform.localPosition = GetStampLocalPosition(item);
            stamp.transform.localRotation = Quaternion.Euler(75f, 0f, 0f);
            stamp.transform.localScale = Vector3.one * 0.16f;

            var text = stamp.AddComponent<TextMesh>();
            text.text = "SELL";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.2f;
            text.fontSize = 72;
            text.color = new Color(0.75f, 0.05f, 0.03f, 1f);
        }

        internal static void RemoveStamp(ShipItem item)
        {
            if (!item)
                return;

            var stamp = item.transform.Find(StampName);
            if (stamp)
                Object.Destroy(stamp.gameObject);
        }

        private static bool HasStamp(ShipItem item)
        {
            return item && item.transform.Find(StampName) != null;
        }

        private static bool IsEligibleCargo(ShipItem item)
        {
            if (!item)
                return false;

            if (!item.sold || item.nailed || item.held != null)
                return false;

            if (!IsCargoOnActiveVessel(item))
                return false;

            var good = item.GetComponent<Good>();
            return good != null && good.GetMissionIndex() == -1;
        }

        private static bool IsCargoOnActiveVessel(ShipItem item)
        {
            if (!item || !CrewBoatContextResolver.TryResolveBoatTransforms(out var topBoat, out var worldBoat))
                return false;

            if (item.currentActualBoat && item.currentActualBoat == worldBoat)
                return true;

            if (item.transform.IsChildOf(worldBoat) || item.transform.IsChildOf(topBoat))
                return true;

            var saveable = item.GetComponent<SaveablePrefab>();
            var vesselSaveable = topBoat ? topBoat.GetComponent<SaveableObject>() : null;
            return saveable && vesselSaveable && saveable.GetParentObject() == vesselSaveable.sceneIndex;
        }

        private static bool IsCargoValidForSale(ShipItem item)
        {
            if (!item)
                return false;

            var saveable = item.GetComponent<SaveablePrefab>();
            if (!saveable || PrefabsDirectory.instance == null || PrefabsDirectory.instance.directory == null)
                return false;

            var crate = item.GetComponent<ShipItemCrate>();
            if (crate)
            {
                float fullAmount = PrefabsDirectory.instance.directory[saveable.prefabIndex]
                    .GetComponent<ShipItemCrate>().amount;
                if (crate.amount < fullAmount)
                    return false;
            }

            var bottle = item.GetComponent<ShipItemBottle>();
            if (bottle)
            {
                float fullHealth = PrefabsDirectory.instance.directory[saveable.prefabIndex]
                    .GetComponent<ShipItemBottle>().health;
                if (bottle.health < fullHealth)
                    return false;
            }

            var salt = item.GetComponent<ShipItemSalt>();
            if (salt)
            {
                float fullAmount = PrefabsDirectory.instance.directory[saveable.prefabIndex]
                    .GetComponent<ShipItemSalt>().amount;
                if (salt.amount < fullAmount)
                    return false;
            }

            return true;
        }

        private static bool CanSellCargoAtMarket(ShipItem item, IslandMarket market)
        {
            if (market == null || !IsCargoValidForSale(item))
                return false;

            Port port = market.GetPort();
            if (port == null)
                return false;

            if (PlayerReputation.GetRepLevel(port.region) < 1)
                return false;

            int currency = GameState.currentCurrency;
            return market.allowCurrencyConversion || currency == market.GetPortRegion();
        }

        private static int GetSellPrice(IslandMarket market, int goodIndex, int currency)
        {
            int rawPrice = market.knownPrices[market.GetPortIndex()].sellPrices[goodIndex];
            bool withConversionFee = market.GetPort().region != (PortRegion)currency;
            return CurrencyMarket.instance.GetSellPriceInCurrency((Currency)currency, rawPrice, withConversionFee);
        }

        private static void LogTransaction(ShipItem item, int goodIndex, int sellPrice, int currency, string portName)
        {
            if (DayLogs.instance != null
                && DayLogs.instance.dayLogs != null
                && currency >= 0
                && currency < DayLogs.instance.dayLogs.Length)
            {
                int prefabIndex = PrefabsDirectory.GoodToItemIndex(goodIndex);
                var prefab = PrefabsDirectory.instance.directory[prefabIndex];
                var logItem = prefab ? prefab.GetComponent<ShipItem>() : item;
                DayLogs.instance.dayLogs[currency].LogTransaction(sellPrice, logItem);
            }

            AddReceiptTransaction(goodIndex, -1, sellPrice, currency, portName);
        }

        private static void AddReceiptTransaction(int goodIndex, int amount, int price, int currency, string portName)
        {
            var scribe = EconomyUIReceiptScribe.instance;
            if (scribe == null)
                return;

            if (scribe.currentReceipt == null
                || scribe.currentReceipt.day < GameState.day - 1
                || scribe.currentReceipt.portName != portName)
                scribe.currentReceipt = new TradeReceipt();

            var receipt = scribe.currentReceipt;
            int existing = -1;
            int empty = -1;
            for (int i = 0; i < receipt.goods.Length; i++)
            {
                if (receipt.goods[i] == goodIndex
                    && receipt.tradeCurrencies[i] == currency
                    && receipt.tradeAmounts[i] < 0)
                {
                    existing = i;
                    break;
                }

                if (empty == -1 && receipt.goods[i] <= 0)
                    empty = i;
            }

            int slot = existing;
            if (slot == -1)
            {
                if (empty == -1)
                {
                    TradeReceiptsUI.instance?.ReceiveReceipt(receipt);
                    receipt = new TradeReceipt();
                    scribe.currentReceipt = receipt;
                    slot = 0;
                }
                else
                {
                    slot = empty;
                }

                receipt.goods[slot] = goodIndex;
                receipt.tradeAmounts[slot] = amount;
                receipt.tradeTotals[slot] = price;
                receipt.tradeCurrencies[slot] = currency;
                receipt.day = GameState.day;
                receipt.portName = portName;
            }
            else
            {
                receipt.tradeTotals[slot] += price;
                receipt.tradeAmounts[slot] += amount;
                receipt.day = GameState.day;
            }
        }

        private static Vector3 GetStampLocalPosition(ShipItem item)
        {
            var renderer = item.GetComponent<Renderer>();
            if (renderer == null)
                return Vector3.up * 0.35f;

            Vector3 localTop = item.transform.InverseTransformPoint(renderer.bounds.center + Vector3.up * renderer.bounds.extents.y);
            return localTop + Vector3.up * 0.03f;
        }
    }
}
