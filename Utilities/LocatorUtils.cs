using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal static class LocatorUtils
    {
        private const float LegacyBedScanRadius = 20f;
        private const float DuplicateBedSleepPositionTolerance = 0.25f;

        public static int[] findItemCounts(string[] targetItemNames)
        {
            Transform vesselReference = CrewBoatContextResolver.GetActiveTopBoat()
                ?? CrewBoatContextResolver.GetActiveWorldBoat();
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
                    bool isClose = vesselReference
                        && (item.transform.position - vesselReference.position).sqrMagnitude <= maxDistSqr;

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

        public static bool[] FindItemsOnCurrentVessel(string[] targetItemNames)
        {
            if (targetItemNames == null || targetItemNames.Length == 0)
                return new bool[0];

            bool[] found = new bool[targetItemNames.Length];
            ShipItem[] allItems = GameObject.FindObjectsOfType<ShipItem>();
            foreach (ShipItem item in allItems)
            {
                if (!IsItemAvailableOnCurrentVessel(item))
                    continue;

                for (int i = 0; i < targetItemNames.Length; i++)
                {
                    if (!found[i] && item.name == targetItemNames[i])
                        found[i] = true;
                }
            }

            return found;
        }

        public static List<UnityEngine.Component> FindBedsOnBoat()
        {
            var beds = new List<UnityEngine.Component>();
            var sleepTransforms = new HashSet<Transform>();
            var sleepPositions = new List<Vector3>();

            foreach (ShipItemBed bed in GameObject.FindObjectsOfType<ShipItemBed>())
            {
                if (IsBedOnCurrentVessel(bed))
                    AddUniqueBed(beds, sleepTransforms, sleepPositions, bed);
            }

            foreach (GPButtonBed bed in GameObject.FindObjectsOfType<GPButtonBed>())
            {
                if (IsBedOnCurrentVessel(bed))
                    AddUniqueBed(beds, sleepTransforms, sleepPositions, bed);
            }

            return beds;
        }

        public static int CountBeds()
        {
            return FindBedsOnBoat().Count;
        }

        public static float FindBestSpyglassZoomOnCurrentVessel()
        {
            float bestZoom = 1f;

            foreach (var spyglass in GameObject.FindObjectsOfType<ShipItemSpyglass>())
            {
                if (!IsItemAvailableOnCurrentVessel(spyglass))
                    continue;

                float zoom = Traverse.Create(spyglass).Field("maxZoom").GetValue<float>();

                // Patch for the level 2 spyglass which has a huge zoom for some reason
                if (zoom > 15)
                {
                    zoom = 4f;
                }

                if (zoom > bestZoom)
                {
                    bestZoom = zoom;
                }
            }

            return bestZoom;
        }

        public static float FindBestLookoutSpyglassZoomOnCurrentVessel()
        {
            // This used to divide by 5, but it looks like we might just need to patch the level 2 spyglass.
            return Mathf.Max(1f, FindBestSpyglassZoomOnCurrentVessel() / 1f);
        }

        private static bool IsItemAvailableOnCurrentVessel(ShipItem item)
        {
            if (!item)
                return false;

            if (ShipItemBelongsToCurrentVessel(item))
                return true;

            if (item.GetCurrentInventorySlot() != -1 || item.held != null)
                return true;

            return IsNearCurrentVesselReference(item.transform);
        }

        private static bool IsBedOnCurrentVessel(ShipItemBed bed)
        {
            if (!bed)
                return false;

            return IsItemAvailableOnCurrentVessel(bed);
        }

        private static bool IsBedOnCurrentVessel(GPButtonBed bed)
        {
            if (!bed)
                return false;

            if (bed.GetComponentInParent<ShipItemBed>())
                return false;

            return IsTransformOnCurrentVessel(bed.transform) || IsNearCurrentVesselReference(bed.transform);
        }

        private static void AddUniqueBed(
            List<UnityEngine.Component> beds,
            HashSet<Transform> sleepTransforms,
            List<Vector3> sleepPositions,
            UnityEngine.Component bed)
        {
            Transform sleepTransform = GetBedSleepTransform(bed);
            if (sleepTransform && !sleepTransforms.Add(sleepTransform))
                return;

            Vector3 sleepPosition = sleepTransform ? sleepTransform.position : bed.transform.position;
            float toleranceSqr = DuplicateBedSleepPositionTolerance * DuplicateBedSleepPositionTolerance;
            foreach (var existingPosition in sleepPositions)
            {
                if ((existingPosition - sleepPosition).sqrMagnitude <= toleranceSqr)
                    return;
            }

            sleepPositions.Add(sleepPosition);
            beds.Add(bed);
        }

        private static Transform GetBedSleepTransform(UnityEngine.Component bed)
        {
            var shipBed = bed as ShipItemBed;
            if (shipBed && shipBed.sleepPos)
                return shipBed.sleepPos;

            var buttonBed = bed as GPButtonBed;
            if (buttonBed && buttonBed.sleepPos)
                return buttonBed.sleepPos;

            return bed ? bed.transform : null;
        }

        private static bool ShipItemBelongsToCurrentVessel(ShipItem item)
        {
            if (!item)
                return false;

            if (IsTransformOnCurrentVessel(item.transform)
             || IsTransformOnCurrentVessel(item.currentActualBoat)
             || IsTransformOnCurrentVessel(item.currentWalkCol))
                return true;

            int sceneIndex = GetCurrentVesselSceneIndex();
            if (sceneIndex >= 0)
            {
                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable && saveable.GetParentObject() == sceneIndex)
                    return true;
            }

            return false;
        }

        private static bool IsTransformOnCurrentVessel(Transform transform)
        {
            if (!transform)
                return false;

            Transform worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            if (worldBoat && (transform == worldBoat || transform.IsChildOf(worldBoat)))
                return true;

            Transform topBoat = CrewBoatContextResolver.GetActiveTopBoat();
            return topBoat && (transform == topBoat || transform.IsChildOf(topBoat));
        }

        private static int GetCurrentVesselSceneIndex()
        {
            Transform topBoat = CrewBoatContextResolver.GetActiveTopBoat();
            if (!topBoat)
                return -1;

            var saveable = topBoat.GetComponent<SaveableObject>();
            return saveable ? saveable.sceneIndex : -1;
        }

        private static bool IsNearCurrentVesselReference(Transform transform)
        {
            if (!transform)
                return false;

            Transform reference = CrewBoatContextResolver.GetActiveTopBoat()
                ?? CrewBoatContextResolver.GetActiveWorldBoat();
            if (!reference)
                return false;

            float maxDistSqr = LegacyBedScanRadius * LegacyBedScanRadius;
            return (transform.position - reference.position).sqrMagnitude <= maxDistSqr;
        }
    }
}
