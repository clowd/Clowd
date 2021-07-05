using System;

namespace Clowd.PlatformUtil
{
    public interface IScreen : IEquatable<IScreen>
    {
        nint Handle { get; }
        int Index { get; }
        int DpiX { get; }
        bool IsPrimary { get; }
        bool IsVirtual { get; }
        ScreenRect Bounds { get; }
        ScreenRect WorkingArea { get; }
    }
}
