namespace SailwindVirtualCrew
{
    public class PilotTask
    {
        public Crewman AssignedCrewman { get; }

        public PilotTask(Crewman crewman)
        {
            AssignedCrewman     = crewman;
            crewman.CurrentTask = this;
            CrewNavigationCoordinator.Instance.BeginPilot(this);
        }

        public void Cancel()
        {
            CrewNavigationCoordinator.Instance.Cancel(this);
            if (AssignedCrewman.CurrentTask == this)
                AssignedCrewman.CurrentTask = null;
        }
    }
}
