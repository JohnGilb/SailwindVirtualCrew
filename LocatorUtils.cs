using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class LocatorUtils
    {
        public static int[] findItemCounts(string[] targetItemNames)
        {
            Vector3 playerPos = GameState.currentBoat.transform.position;
            float maxDistSqr = 100f * 100f;

            int[] counts = new int[targetItemNames.Length];

            ShipItem[] allItems = GameObject.FindObjectsOfType<ShipItem>();

            foreach (ShipItem item in allItems)
            {
                for (int i = 0; i < targetItemNames.Length; i++)
                {
                    if (item.name != targetItemNames[i])
                        continue;

                    bool inInventory = item.GetCurrentInventorySlot() != -1 || item.held != null;
                    float distSqr = (item.transform.position - playerPos).sqrMagnitude;
                    bool isClose = distSqr <= maxDistSqr;

                    if (inInventory || isClose)
                        counts[i]++;
                }
            }

            return counts;
        }

        public static bool[] findItem(string[] targetItemNames)
        {
            int[] counts = findItemCounts(targetItemNames);
            bool[] found = new bool[counts.Length];
            for (int i = 0; i < counts.Length; i++)
                found[i] = counts[i] > 0;
            return found;
        }
    }
}
