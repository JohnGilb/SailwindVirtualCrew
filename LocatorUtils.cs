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
        public static bool[] findItem(string[] targetItemNames)
        {
            Vector3 playerPos = GameState.currentBoat.transform.position;
            float maxDistSqr = 100f * 100f;

            // Result array: one bool per target item name
            bool[] foundItems = new bool[targetItemNames.Length];

            // Find all ShipItem instances
            ShipItem[] allItems = GameObject.FindObjectsOfType<ShipItem>();

            foreach (ShipItem item in allItems)
            {
                for (int i = 0; i < targetItemNames.Length; i++)
                {
                    // Skip if already found
                    if (foundItems[i])
                        continue;

                    // Name match?
                    if (item.name != targetItemNames[i])
                        continue;

                    // Check 1: In inventory or held
                    bool inInventory = item.GetCurrentInventorySlot() != -1 || item.held != null;

                    // Check 2: Within 100 meters
                    float distSqr = (item.transform.position - playerPos).sqrMagnitude;
                    bool isClose = distSqr <= maxDistSqr;

                    if (inInventory || isClose)
                    {
                        foundItems[i] = true;

                        Console.WriteLine(
                            string.Format(
                                "Item name:{0}, InventoryPos:{1}, Distance:{2:F2}",
                                item.name,
                                item.GetCurrentInventorySlot(),
                                Mathf.Sqrt(distSqr)));

                        Console.WriteLine("----This can be used for navigation!");
                    }
                }
            }

            return foundItems;
        }
    }
}
