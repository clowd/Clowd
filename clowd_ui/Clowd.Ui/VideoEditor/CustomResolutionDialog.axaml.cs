using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Clowd.UI.Helpers;
using Clowd.VideoSDK.Editing;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// The resolution picker's "Custom…" row: two spinners over the current canvas size. The value
    /// it returns is already clamped and evened by <see cref="EditorSession.ClampOutputDimension"/>,
    /// so the caller can hand it straight to <see cref="EditorSession.SetOutputSize"/>.
    /// </summary>
    public partial class CustomResolutionDialog : Window
    {
        // satisfies the XAML compiler's runtime-loader check (AVLN3001); the dialog is only ever
        // created through ShowAsync.
        [Obsolete("Runtime-loader signature only — use CustomResolutionDialog.ShowAsync.", error: true)]
        public CustomResolutionDialog()
        {
            throw new NotSupportedException("CustomResolutionDialog requires a starting size.");
        }

        private CustomResolutionDialog(int widthPx, int heightPx)
        {
            InitializeComponent();
            Icon = AppStyles.AppIcon;

            WidthBox.Min = EditorSession.MinOutputDimension;
            WidthBox.Max = EditorSession.MaxOutputDimension;
            HeightBox.Min = EditorSession.MinOutputDimension;
            HeightBox.Max = EditorSession.MaxOutputDimension;
            WidthBox.Value = widthPx;
            HeightBox.Value = heightPx;

            OkButton.Click += (_, _) => Close(Accepted());
            CancelButton.Click += (_, _) => Close(null);

            Opened += (_, _) => WidthBox.Focus();

            // Cmd+W is the macOS close gesture — cancels, same as the Cancel button (issue #73)
            MacWindowShortcuts.AddCloseShortcut(this, () => Close(null));
        }

        /// <summary>Prompts for a size, starting from the one passed in. Null means the user
        /// canceled (or closed the window), and nothing should change.</summary>
        public static async Task<(int WidthPx, int HeightPx)?> ShowAsync(Window owner, int widthPx, int heightPx)
        {
#pragma warning disable CS0618 // the private constructor is the intended one
            var dialog = new CustomResolutionDialog(widthPx, heightPx);
#pragma warning restore CS0618

            if (owner is { IsVisible: true })
                return await dialog.ShowDialog<(int, int)?>(owner);

            var closed = new TaskCompletionSource<(int, int)?>();
            dialog.Closed += (_, _) => closed.TrySetResult(null);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            return await closed.Task;
        }

        private (int, int)? Accepted() => (
            EditorSession.ClampOutputDimension(ToPixels(WidthBox.Value)),
            EditorSession.ClampOutputDimension(ToPixels(HeightBox.Value)));

        private static int ToPixels(double value) =>
            (int)Math.Round(Math.Clamp(value, EditorSession.MinOutputDimension, EditorSession.MaxOutputDimension));
    }
}
