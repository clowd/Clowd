namespace NotifyIconLib.Interop
{
    /// <summary>
    /// Event flags for clicked events.
    /// </summary>
    public enum MouseEvent
    {
        /// <summary>
        /// The user requested the tray icon’s context menu (either by right-clicking or by pressing the Context key).
        /// </summary>
        ContextMenu,

        /// <summary>
        /// The user activated the tray icon, either by clicking it or by pressing the Space key or Enter key. In the case of the Enter key, the event is generated twice in quick succession.
        /// </summary>
        Activated,

        /// <summary>
        /// The mouse was moved withing the
        /// taskbar icon's area.
        /// </summary>
        MouseMove,

        /// <summary>
        /// The right mouse button was clicked.
        /// </summary>
        IconRightMouseDown,

        /// <summary>
        /// The left mouse button was clicked.
        /// </summary>
        IconLeftMouseDown,

        /// <summary>
        /// The right mouse button was released.
        /// </summary>
        IconRightMouseUp,

        /// <summary>
        /// The left mouse button was released.
        /// </summary>
        IconLeftMouseUp,

        /// <summary>
        /// The middle mouse button was clicked.
        /// </summary>
        IconMiddleMouseDown,

        /// <summary>
        /// The middle mouse button was released.
        /// </summary>
        IconMiddleMouseUp,

        /// <summary>
        /// The taskbar icon was double clicked.
        /// </summary>
        IconDoubleClick,

        /// <summary>
        /// The balloon tip was clicked.
        /// </summary>
        BalloonToolTipClicked
    }
}