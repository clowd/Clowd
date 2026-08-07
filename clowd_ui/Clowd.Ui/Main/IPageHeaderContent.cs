using Avalonia.Controls;

namespace Clowd.UI
{
    /// <summary>
    /// A page that puts a control of its own beside the window's page title — the Recent page's
    /// filter, which the issue asks for "to the right of the Recent header" and so cannot live in the
    /// page's own content. The window shows <see cref="HeaderContent"/> while that page is selected
    /// and clears the slot when another one is.
    /// </summary>
    /// <remarks>
    /// Pages are created once and cached for the window's lifetime, so the same control instance is
    /// handed back to the header every time its page is selected — implementations should return a
    /// control they own rather than building a fresh one per call.
    /// </remarks>
    public interface IPageHeaderContent
    {
        Control HeaderContent { get; }
    }
}
