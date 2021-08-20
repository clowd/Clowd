using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.SHCore;
using static Vanara.PInvoke.Shell32;

namespace Clowd.PlatformUtil.Windows
{
    public unsafe static class Explorer
    {
        private const string ERR_FNF = "The specified file or folder doesn't exist.";

        private static void SelectFolderItems(PIDL folder, IntPtr[] targets, bool edit)
        {
            OFASI of = edit ? OFASI.OFASI_EDIT : OFASI.OFASI_NONE;
            SHOpenFolderAndSelectItems(folder, (uint)(targets?.Length ?? 0), targets, of).ThrowIfFailed();
        }

        public static void SelectItems(string[] fileOrFolderPaths)
        {
            if (fileOrFolderPaths == null)
                throw new ArgumentNullException(nameof(fileOrFolderPaths));

            if (fileOrFolderPaths.Length == 0)
                return;

            // we need to group up the paths by parent folder
            var explorerWindows = fileOrFolderPaths
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Select(Path.GetFullPath)
                .Select(p =>
                {
                    if (Directory.Exists(p))
                    {
                        return (FileSystemInfo)new DirectoryInfo(p);
                    }
                    else if (File.Exists(p))
                    {
                        return (FileSystemInfo)new FileInfo(p);
                    }
                    else
                    {
                        throw new FileNotFoundException(ERR_FNF, p);
                    }
                })
                .GroupBy(p => Path.GetDirectoryName(p.FullName));

            // open one explorer window for each group
            foreach (var paths in explorerWindows)
            {
                using var pidlParent = new PIDL(paths.Key);
                var children = paths.ToArray().Select(p => new PIDL(p.FullName)).ToArray();

                try
                {
                    var childrenPtr = children.Select(p => (IntPtr)p).ToArray();
                    SelectFolderItems(pidlParent, childrenPtr, false);
                }
                finally
                {
                    foreach (var c in children)
                        c.Dispose();
                }
            }
        }

        public static void SelectSingleItem(string fullPath, bool edit)
        {
            if (fullPath == null)
                throw new ArgumentNullException(nameof(fullPath));

            fullPath = Path.GetFullPath(fullPath);

            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            if (!exists)
                throw new ArgumentException(ERR_FNF, nameof(fullPath));

            using var pidlParent = new PIDL(fullPath);
            SelectFolderItems(pidlParent, null, edit);
        }
    }
}
