using UnityEngine;

namespace SailwindVirtualCrew
{
    public enum SailStorageRequestKind
    {
        Store,
        Restore
    }

    public class SailStorageRequest
    {
        public SailStorageRequestKind Kind { get; }
        public ICommonSailActions Sail { get; }
        public StowedSailSaveData StowedData { get; }
        public WorkRequestStatus Status { get; set; }
        public Crewman AssignedCrewman { get; set; }
        public GPButtonRopeWinch HalyardWinch { get; private set; }

        public string SailIdentifier => Sail != null ? Sail.getDefaultIdentifier() : StowedData?.sailIdentifier;
        public string CommandName => Kind == SailStorageRequestKind.Store ? "Store Sail in Locker" : "Restore Sail from Locker";

        private float positioningStartTime;
        private float positioningTimeTotal;
        private bool concretePositioning;
        private float workStartTime;
        private float workDuration;

        public SailStorageRequest(ICommonSailActions sail)
        {
            Kind = SailStorageRequestKind.Store;
            Sail = sail;
            HalyardWinch = sail?.getHalyardWinch();
            Status = WorkRequestStatus.Open;
        }

        public SailStorageRequest(StowedSailSaveData data)
        {
            Kind = SailStorageRequestKind.Restore;
            StowedData = data;
            HalyardWinch = SailStorageService.FindHalyardWinch(data);
            Status = WorkRequestStatus.Open;
        }

        public void BeginPositioning(Crewman crewman)
        {
            AssignedCrewman = crewman;
            HalyardWinch = Kind == SailStorageRequestKind.Store
                ? Sail?.getHalyardWinch()
                : SailStorageService.FindHalyardWinch(StowedData);

            positioningTimeTotal = 7f - crewman.Dexterity;
            positioningStartTime = Time.time;
            concretePositioning = HalyardWinch != null
                && CrewNavigationCoordinator.Instance.TryBeginWinchPositioning(this, crewman, HalyardWinch);
            Status = WorkRequestStatus.Positioning;
            crewman.CurrentTask = this;
        }

        public bool IsPositioningComplete()
        {
            if (concretePositioning)
                return CrewNavigationCoordinator.Instance.IsPositioningComplete(this);

            return Time.time >= positioningStartTime + positioningTimeTotal;
        }

        public float GetPositioningProgress()
        {
            return concretePositioning
                ? CrewNavigationCoordinator.Instance.GetPositioningProgress(this)
                : positioningTimeTotal <= 0f ? 0f
                    : Mathf.Clamp01(1f - (Time.time - positioningStartTime) / positioningTimeTotal) * 100f;
        }

        public void Begin()
        {
            if (concretePositioning)
            {
                CrewNavigationCoordinator.Instance.Complete(this);
                concretePositioning = false;
            }

            workDuration = Mathf.Max(5f, 60f - AssignedCrewman.Strength * 5f);
            workStartTime = Time.time;
            Status = WorkRequestStatus.InProgress;
        }

        public bool IsComplete() => Time.time >= workStartTime + workDuration;

        public float GetProgress()
        {
            if (workDuration <= 0f)
                return 100f;

            return Mathf.Clamp01((Time.time - workStartTime) / workDuration) * 100f;
        }

        public void CancelPositioning()
        {
            if (!concretePositioning)
                return;

            CrewNavigationCoordinator.Instance.Cancel(this);
            concretePositioning = false;
        }

        public string DisplayLabel
        {
            get
            {
                if (Sail != null)
                    return CommandName + " - " + Sail.getSailName();
                string name = StowedData != null && !string.IsNullOrEmpty(StowedData.friendlyName)
                    ? StowedData.friendlyName
                    : StowedData != null && !string.IsNullOrEmpty(StowedData.sailName)
                        ? StowedData.sailName
                        : "Sail";
                return CommandName + " - " + name;
            }
        }
    }
}
