using System.Linq;
using UnityEngine;

namespace SailwindVirtualCrew
{
    public static class SailStorageService
    {
        public const float StoreDeploymentLimit = 0.05f;

        public static bool CanStore(ICommonSailActions sail)
        {
            return IsAttachedStaysail(sail)
                && IsLessThanDeploymentLimit(sail)
                && !VirtualCrewManager.Instance.HasPendingRequestForSail(sail);
        }

        public static bool IsAttachedStaysail(ICommonSailActions sail)
        {
            var realSail = sail?.getRealSail();
            if (realSail == null || realSail.category != SailCategory.staysail)
                return false;

            var mast = realSail.GetComponentInParent<Mast>();
            return mast != null && mast.onlyStaysails;
        }

        public static bool IsLessThanDeploymentLimit(ICommonSailActions sail)
        {
            var halyard = sail?.getHalyardWinch();
            if (halyard == null || halyard.rope == null)
                return false;

            return GetNormalizedHalyardDeployment(halyard) < StoreDeploymentLimit;
        }

        public static float GetNormalizedHalyardDeployment(GPButtonRopeWinch halyard)
        {
            if (halyard == null || halyard.rope == null)
                return 1f;

            float length = Mathf.Clamp01(halyard.rope.currentLength);
            var reef = halyard.rope as RopeControllerSailReef;
            return reef != null && reef.reverseReefing ? 1f - length : length;
        }

        public static StowedSailSaveData Capture(ICommonSailActions sail)
        {
            var realSail = sail?.getRealSail();
            var mast = realSail != null ? realSail.GetComponentInParent<Mast>() : null;
            if (realSail == null || mast == null)
                return null;

            return new StowedSailSaveData
            {
                sailIdentifier = sail.getDefaultIdentifier(),
                mastName = mast.name,
                mastIndex = mast.orderIndex,
                prefabIndex = realSail.prefabIndex,
                installHeight = realSail.GetCurrentInstallHeight(),
                minAngle = realSail.minAngle,
                maxAngle = realSail.maxAngle,
                sailColor = realSail.activeColor,
                scaleY = realSail.GetScaleY(),
                scaleZ = realSail.GetScaleZ(),
                sailName = realSail.sailName,
                friendlyName = sail.FriendlyName
            };
        }

        public static bool Store(ICommonSailActions sail, out StowedSailSaveData stored)
        {
            stored = Capture(sail);
            if (stored == null)
                return false;

            var realSail = sail.getRealSail();
            var mast = realSail.GetComponentInParent<Mast>();
            if (mast == null)
                return false;

            mast.DetachSailFromMast(realSail.gameObject);
            return true;
        }

        public static bool Restore(StowedSailSaveData data)
        {
            var mast = FindMast(data);
            if (mast == null || data == null)
                return false;

            var sailData = new SaveSailData
            {
                prefabIndex = data.prefabIndex,
                mastIndex = data.mastIndex,
                installHeight = data.installHeight,
                minAngle = data.minAngle,
                maxAngle = data.maxAngle,
                health = 100f,
                sailColor = data.sailColor,
                scaleY = data.scaleY,
                scaleZ = data.scaleZ
            };

            mast.LoadSail(sailData);
            ApplyRestoredRopeDefaults(mast, data);
            return true;
        }

        public static Mast FindMast(StowedSailSaveData data)
        {
            if (data == null)
                return null;

            var refs = FindCurrentBoatRefs();
            if (refs != null && refs.masts != null && data.mastIndex >= 0 && data.mastIndex < refs.masts.Length)
            {
                var mast = refs.masts[data.mastIndex];
                if (mast != null)
                    return mast;
            }

            return Object.FindObjectsOfType<Mast>()
                .FirstOrDefault(m => m != null && m.orderIndex == data.mastIndex && m.name == data.mastName);
        }

        public static GPButtonRopeWinch FindHalyardWinch(StowedSailSaveData data)
        {
            var mast = FindMast(data);
            if (mast == null || mast.reefWinch == null || mast.reefWinch.Length == 0)
                return null;

            int index = Mathf.Clamp(FindStowedSailOrderIndex(data, mast), 0, mast.reefWinch.Length - 1);
            return mast.reefWinch[index];
        }

        private static int FindStowedSailOrderIndex(StowedSailSaveData data, Mast mast)
        {
            if (mast == null || data == null)
                return 0;

            int lower = 0;
            foreach (var sailObject in mast.sails)
            {
                if (sailObject == null)
                    continue;

                var sail = sailObject.GetComponent<Sail>();
                if (sail != null && sail.GetCurrentInstallHeight() < data.installHeight)
                    lower++;
            }

            return lower;
        }

        private static BoatRefs FindCurrentBoatRefs()
        {
            var context = CrewBoatContextResolver.Resolve();
            if (context?.TopBoat != null)
                return context.TopBoat.GetComponent<BoatRefs>() ?? context.WorldBoat.GetComponentInParent<BoatRefs>();

            var worldBoat = CrewBoatContextResolver.GetActiveWorldBoat();
            return worldBoat != null ? worldBoat.GetComponentInParent<BoatRefs>() : null;
        }

        private static void ApplyRestoredRopeDefaults(Mast mast, StowedSailSaveData data)
        {
            mast.UpdateControllerAttachments();
            Sail restored = null;
            foreach (var sailObject in mast.sails)
            {
                if (sailObject == null)
                    continue;

                var sail = sailObject.GetComponent<Sail>();
                if (sail != null && sail.prefabIndex == data.prefabIndex
                    && Mathf.Abs(sail.GetCurrentInstallHeight() - data.installHeight) < 0.05f)
                    restored = sail;
            }

            if (restored == null)
                return;

            var connections = restored.GetComponent<SailConnections>();
            SetReefControllerFullyTight(connections?.reefController as RopeControllerSailReef);
            SetControllerLoose(connections?.angleControllerMid);
            SetControllerLoose(connections?.angleControllerLeft);
            SetControllerLoose(connections?.angleControllerRight);
        }

        private static void SetReefControllerFullyTight(RopeControllerSailReef reef)
        {
            if (reef == null)
                return;

            reef.currentLength = reef.reverseReefing ? 1f : 0f;
            reef.changed = true;
        }

        private static void SetControllerLoose(RopeController controller)
        {
            if (controller == null)
                return;

            controller.currentLength = 1f;
            controller.changed = true;
        }
    }
}
