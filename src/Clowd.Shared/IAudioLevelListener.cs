namespace Clowd
{
    public interface IAudioLevelListener : IDisposable
    {
        AudioDeviceInfo Device { get; }
        double PeakLevel { get; }
    }
}
