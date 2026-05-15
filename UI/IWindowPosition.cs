namespace SailwindVirtualCrew
{
    public interface IWindowPosition
    {
        string WindowKey { get; }
        float[] GetPosition();
        void SetPosition(float x, float y, float userHeight);
    }
}
