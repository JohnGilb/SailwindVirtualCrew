namespace SailwindVirtualCrew
{
    public class StowedSail : ICommonSailActions
    {
        private readonly StowedSailSaveData data;

        public StowedSail(StowedSailSaveData data)
        {
            this.data = data;
        }

        public StowedSailSaveData Data => data;
        public string FriendlyName
        {
            get => data.friendlyName;
            set => data.friendlyName = value;
        }

        public string getSailName()
        {
            string name = !string.IsNullOrEmpty(FriendlyName)
                ? FriendlyName
                : !string.IsNullOrEmpty(data.sailName)
                    ? data.sailName
                    : data.sailIdentifier;
            return name + " [Stowed]";
        }

        public string getDefaultIdentifier() => data.sailIdentifier;
        public Sail getRealSail() => null;
        public GPButtonRopeWinch getHalyardWinch() => SailStorageService.FindHalyardWinch(data);
        public void stop() { }
        public void deploySail() { }
        public void reefSail() { }
        public void easeSail() { }
        public void trimSail() { }
    }
}
