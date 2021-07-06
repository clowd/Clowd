using System.Drawing;

namespace Clowd.PlatformUtil.Windows
{
    public struct WindowCreationOptions
    {
        public string Caption;
        public StandardCursor DefaultCursor;
        public WindowClassStyle ClassStyle;
        public WindowStyle WindowStyle;
        public WindowStyleEx ExtendedStyle;
        public Rectangle StartPosition;
        public WindowStartPosition StartPositionMode;
        public bool NoWindowOnlyMessagePump;
        public bool BackgroundThread;
        public bool DisableTransitions;
        public User32Window Parent;
    }
}
