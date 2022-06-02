using System;
using System.Collections.Generic;

namespace Clowd.PlatformUtil
{
    /// <summary>
    /// Shows the current visibility state of the window scroll bars.
    /// </summary>
    [Flags]
    public enum ScrollVisibility
    {
        /// <summary>
        /// No scroll bars are visible.
        /// </summary>
        None = 0,

        /// <summary>
        /// The horizontal scroll bar is visible.
        /// </summary>
        Horizontal = 1,

        /// <summary>
        /// The vertical scroll bar is visible.
        /// </summary>
        Vertical = 2,

        /// <summary>
        /// Both the horizontal and the vertical scroll bar is visible.
        /// </summary>
        Both = Horizontal | Vertical,
    }

    public interface IWindow : IEquatable<IWindow>
    {
        // see https://github.com/AvaloniaUI/Avalonia/blob/7842883961d094e08e9def7f30cf32fd573179c7/src/Avalonia.Controls/Platform/IWindowImpl.cs
        nint Handle { get; }
        
        int ProcessId { get; }
        
        int ThreadId { get; }
        
        string ClassName { get; }
        
        string Caption { get; }
        
        ScreenRect WindowBounds { get; }

        ScreenRect DwmRenderBounds { get; }

        int ZPosition { get; }
        
        ScrollVisibility ScrollBars { get; }
        
        bool IsTopmost { get; }

        bool IsDisabled { get; }

        bool IsMaximized { get; }
        
        bool IsMinimized { get; }
        
        bool IsCurrentVirtualDesktop { get; }
        
        bool IsCurrentProcess { get; }

        IWindow Parent { get; }

        IEnumerable<IWindow> Children { get; }

        bool Activate();
        
        bool Show();
        
        bool Show(bool activate);
        
        bool Hide();
        
        void Close();
        
        void KillProcess();
        
        void SetPosition(ScreenRect newPosition);
        
        void SetEnabled(bool enabled);

        IScreen GetCurrentScreen();
    }
}
