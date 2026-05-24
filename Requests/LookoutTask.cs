namespace SailwindVirtualCrew
{
    public class LookoutTask
    {
        public Crewman AssignedCrewman { get; }
        public bool SuppressFirstLandBell { get; }

        public LookoutTask(Crewman crewman, bool suppressFirstLandBell = false)
        {
            AssignedCrewman     = crewman;
            SuppressFirstLandBell = suppressFirstLandBell;
            crewman.CurrentTask = this;
            CrewNavigationCoordinator.Instance.BeginLookout(this);
        }

        public void Cancel()
        {
            CrewNavigationCoordinator.Instance.Cancel(this);
            if (AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }
    }
}
