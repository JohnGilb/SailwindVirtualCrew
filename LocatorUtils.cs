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

        public static List<UnityEngine.Component> FindBedsOnBoat()
        {
            Vector3 boatPos = GameState.lastBoat.transform.position;
            float maxDistSqr = 20f * 20f;
            var beds = new List<UnityEngine.Component>();

            foreach (ShipItemBed bed in GameObject.FindObjectsOfType<ShipItemBed>())
            {
                bool inInventory = bed.GetCurrentInventorySlot() != -1 || bed.held != null;
                bool isClose = (bed.transform.position - boatPos).sqrMagnitude <= maxDistSqr;
                if (inInventory || isClose)
                    beds.Add(bed);
            }

            foreach (GPButtonBed bed in GameObject.FindObjectsOfType<GPButtonBed>())
            {
                if ((bed.transform.position - boatPos).sqrMagnitude <= maxDistSqr)
                    beds.Add(bed);
            }

            return beds;
        }

        public static int CountBeds()
        {
            Vector3 playerPos = GameState.lastBoat.transform.position;
            float maxDistSqr = 20f * 20f;
            int count = 0;

            foreach (ShipItemBed bed in GameObject.FindObjectsOfType<ShipItemBed>())
            {
                bool inInventory = bed.GetCurrentInventorySlot() != -1 || bed.held != null;
                bool isClose = (bed.transform.position - playerPos).sqrMagnitude <= maxDistSqr;
                if (inInventory || isClose)
                    count++;
            }

            foreach (GPButtonBed bed in GameObject.FindObjectsOfType<GPButtonBed>())
            {
                if ((bed.transform.position - playerPos).sqrMagnitude <= maxDistSqr)
                    count++;
            }

            return count;
        }
    }
}
