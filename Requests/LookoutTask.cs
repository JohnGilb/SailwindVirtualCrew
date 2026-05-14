namespace SailwindVirtualCrew
{
    public class LookoutTask
    {
        public Crewman AssignedCrewman { get; }

        public LookoutTask(Crewman crewman)
        {
            AssignedCrewman     = crewman;
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
