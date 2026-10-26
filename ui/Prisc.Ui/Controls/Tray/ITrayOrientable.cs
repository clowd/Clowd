using Avalonia.Layout;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// A tray child whose own layout changes when the strip rotates. The tray is the single writer of
    /// the axis: on a rotation it pushes its <see cref="Orientation"/> into every child that implements
    /// this, and each child restyles itself through <c>^[Orientation=Vertical]</c> selectors rather than
    /// rebuilding its visual tree.
    /// </summary>
    public interface ITrayOrientable
    {
        Orientation Orientation { get; set; }
    }
}
