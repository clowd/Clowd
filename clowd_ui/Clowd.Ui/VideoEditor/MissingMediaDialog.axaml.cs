using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Clowd.UI.Helpers;
using Clowd.VideoSDK.Editing;
using Clowd.VideoSDK.Model;
using Clowd.VideoSDK.Playback;
using Path = System.IO.Path;

namespace Clowd.UI.VideoEditor
{
    /// <summary>
    /// Shown when an edit is opened whose media has moved: one row per missing (referenced) source,
    /// each offering the three things that can be done about it —
    /// <list type="bullet">
    /// <item><b>Locate…</b> — pick the file where it lives now; it is reprobed and the source
    /// relinked, and a file whose stream shape no longer matches is accepted with a warning rather
    /// than refused (the items keep their old stream descriptions, see
    /// <see cref="EditorSession.RelinkSource"/>).</item>
    /// <item><b>Remove</b> — drop the file's items, and the rows they emptied, from the edit.</item>
    /// <item><b>Skip</b> — leave it in place but hide/mute its rows, so the project still opens and
    /// plays; the timeline dims them, and the render still refuses until the file is found or
    /// removed.</item>
    /// </list>
    /// Every action goes through the session, so each is its own undo entry and is persisted like
    /// any other edit. The dialog closes itself once nothing is left unresolved.
    /// </summary>
    public partial class MissingMediaDialog : Window
    {
        private readonly EditorSession _session;
        private readonly List<Guid> _unresolved = new List<Guid>();

        // satisfies the XAML compiler's runtime-loader check (AVLN3001); the dialog is only ever
        // created through ShowAsync.
        [Obsolete("Runtime-loader signature only — use MissingMediaDialog.ShowAsync.", error: true)]
        public MissingMediaDialog()
        {
            throw new NotSupportedException("MissingMediaDialog requires an editing session.");
        }

        private MissingMediaDialog(EditorSession session)
        {
            _session = session;

            InitializeComponent();
            Icon = AppStyles.AppIcon;

            CloseButton.Click += (_, _) => Close();

            foreach (var source in session.GetMissingSources())
            {
                _unresolved.Add(source.Id);
                RowsPanel.Children.Add(BuildRow(source));
            }
        }

        /// <summary>Shows the dialog for whatever <see cref="EditorSession.GetMissingSources"/>
        /// currently reports, and returns when the user is done with it. A no-op (and no window)
        /// when nothing is missing.</summary>
        public static async Task ShowAsync(Window owner, EditorSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            if (session.GetMissingSources().Count == 0)
                return;

#pragma warning disable CS0618 // the private constructor is the intended one
            var dialog = new MissingMediaDialog(session);
#pragma warning restore CS0618

            if (owner is { IsVisible: true })
                await dialog.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                await closed.Task;
            }
        }

        // ====================================================================
        // Rows
        // ====================================================================

        private Control BuildRow(Source source)
        {
            var sourceId = source.Id;
            var path = source.Path ?? "";
            var fileName = SafeFileName(path);

            var status = new TextBlock { Classes = { "status" }, IsVisible = false };

            var locate = new Button { Content = "Locate…" };
            var remove = new Button { Content = "Remove" };
            var skip = new Button { Content = "Skip" };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { locate, remove, skip },
            };

            void Resolve(string text)
            {
                buttons.IsVisible = false;
                status.Text = text;
                status.IsVisible = true;

                _unresolved.Remove(sourceId);
                if (_unresolved.Count == 0)
                    Close();
            }

            locate.Click += async (_, _) =>
            {
                buttons.IsEnabled = false;
                try
                {
                    var relinked = await LocateAsync(sourceId, path, fileName);
                    if (relinked != null)
                        Resolve("Relinked to " + relinked);
                }
                finally
                {
                    buttons.IsEnabled = true;
                }
            };

            remove.Click += (_, _) =>
            {
                _session.RemoveSource(sourceId, this);
                Resolve("Removed from the edit.");
            };

            skip.Click += (_, _) =>
            {
                SkipSource(sourceId);
                Resolve("Skipped — its rows are hidden until the file is found.");
            };

            return new Border
            {
                Padding = new Avalonia.Thickness(10, 8),
                CornerRadius = new Avalonia.CornerRadius(4),
                Background = this.FindResource("SemiColorFill0") as Avalonia.Media.IBrush,
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = fileName, FontWeight = Avalonia.Media.FontWeight.Bold },
                        new TextBlock { Classes = { "path" }, Text = String.IsNullOrEmpty(path) ? "(no path)" : path },
                        buttons,
                        status,
                    },
                },
            };
        }

        // ====================================================================
        // Actions
        // ====================================================================

        /// <summary>Picks the file's new home, reprobes it and relinks the source. Returns the file
        /// name it was relinked to, or null when the user canceled or the file could not be
        /// read.</summary>
        private async Task<string> LocateAsync(Guid sourceId, string oldPath, string fileName)
        {
            var directory = SafeDirectory(oldPath);
            var picked = await NiceDialog.ShowSelectFilesDialog(this, "Locate " + fileName,
                directory, filter: new[] { MediaFileTypes.AnyMedia, FilePickerFileTypes.All },
                suggestedFileName: fileName);
            if (picked is not { Length: > 0 })
                return null;

            var newPath = picked[0];

            MediaProbeResult probe;
            try
            {
                probe = await Task.Run(() => MediaProbe.ProbeDetailed(newPath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Relink probe failed: " + ex);
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "That file could not be read: " + ex.Message, "Can't use this file");
                return null;
            }

            var notes = _session.RelinkSource(sourceId, newPath, probe, this);

            // a Skip (this session or an earlier one) hid/muted the file's rows "until the file
            // is found" — it just was, so bring them back. A no-op (no undo entry) when nothing
            // was skipped; undoable when it was.
            _session.SetSourceRowsEnabled(sourceId, true, this);

            if (notes.Count > 0)
            {
                // the relink still happened — the items kept their original stream descriptions —
                // but what plays from here on may not be what the edit was built against.
                await NiceDialog.ShowNoticeAsync(this, NiceDialogIcon.Warning,
                    "The file was relinked, but it does not have the same tracks as the original:" +
                    Environment.NewLine + String.Join(Environment.NewLine, notes),
                    "This file is not quite the same");
            }

            return SafeFileName(newPath);
        }

        /// <summary>Hides (video) or mutes (audio) every row that plays the missing file, which is
        /// what lets the player open a project it cannot fully decode. One session call so a
        /// multi-stream skip is one undo entry, and so a later Locate can symmetrically bring the
        /// rows back (see <see cref="LocateAsync"/>).</summary>
        private void SkipSource(Guid sourceId) => _session.SetSourceRowsEnabled(sourceId, false, this);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Escape leaves everything as it is — which is the same as skipping nothing: the edit
            // opens with the missing file still missing, and the render will say so.
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            base.OnKeyDown(e);
        }

        private static string SafeFileName(string path)
        {
            try
            {
                var name = Path.GetFileName(path);
                return String.IsNullOrEmpty(name) ? "This file" : name;
            }
            catch
            {
                return "This file";
            }
        }

        private static string SafeDirectory(string path)
        {
            try
            {
                return Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
