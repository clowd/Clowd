using System;

namespace Clowd.PlatformUtil.Windows
{
    [Flags]
    public enum WindowStartPosition
    {
        SpecifiedPosition = 0,
        DefaultPosition = 1,
        DefaultSize = 2,
        DefaultPositionAndSize = DefaultPosition | DefaultSize,
    }
}
