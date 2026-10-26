using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Clowd.UI.Helpers
{
    /// <summary>Files dragged in from Explorer/Finder. Both editors take the same kinds of file
    /// their own Add/Import pickers offer (see <c>MediaFileTypes</c>), so a drop asks the picker
    /// filters' question of every path — is this extension one we accept — and the editors report
    /// the ones that are not with a toast rather than silently swallowing the drop.</summary>
    public static class FileDrop
    {
        /// <summary>The local paths in a drag, in drop order. A drag carrying no files (text, a
        /// control's own drag payload), a dropped folder, and a shell item with no local path (a
        /// virtual or not-yet-downloaded item) are all left out, so an empty result is every
        /// caller's "this drag is not for us".</summary>
        public static IReadOnlyList<string> GetLocalPaths(DragEventArgs e)
        {
            var items = e?.DataTransfer?.TryGetFiles();
            if (items == null)
                return Array.Empty<string>();

            var paths = new List<string>();
            foreach (var item in items) {
                string path = null;
                try { path = item.TryGetLocalPath(); } catch {; }
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                    paths.Add(path);
            }

            return paths;
        }
    }
}
