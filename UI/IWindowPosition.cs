namespace SailwindVirtualCrew
{
    public interface IWindowPosition
    {
        string WindowKey { get; }
        float[] GetPosition();
        float[] GetDefaultPosition();
        void SetPosition(float x, float y, float userHeight);
    }
}
