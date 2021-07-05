using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CsWin32.UI.Shell;
using CsWin32.Foundation;
using static CsWin32.Constants;
using static CsWin32.PInvoke;

namespace Clowd.PlatformUtil.Windows
{
    public unsafe static class Explorer
    {
        private const string ERR_FNF = "The specified file or folder doesn't exist.";

        private static IShellFolder GetDesktopFolder()
        {
            IShellFolder desktop;
            Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktop));
            return desktop;
        }

        private static IShellFolder GetFolderFromPIDL(IShellFolder parent, ITEMIDLIST* pidl)
        {
            var guid = typeof(IShellFolder).GUID;
            parent.BindToObject(pidl, null, &guid, out var ppv);
            return (IShellFolder)ppv;

            //void* folderPtr;
            //parent.BindToObject(pidl, null, &guid, &folderPtr);
            //return (IShellFolder)Marshal.GetObjectForIUnknown((IntPtr)folderPtr);
        }

        private static ITEMIDLIST* GetShellFolderChildrenRelativePIDL(IShellFolder parentFolder, string displayName)
        {
            uint pchEaten, pdwAttributes;
            ITEMIDLIST* pList;
            fixed (char* pName = displayName)
                parentFolder.ParseDisplayName(new HWND(IntPtr.Zero), null, pName, &pchEaten, &pList, &pdwAttributes);
            return pList;
        }

        private static void SelectFolderItems(ITEMIDLIST* folder, ITEMIDLIST*[] targets, bool edit)
        {
            fixed (ITEMIDLIST** pTargets = targets)
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, (uint)(targets?.Length ?? 0), pTargets, edit ? OFASI_EDIT : 0));
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
            var desktop = GetDesktopFolder();
            foreach (var paths in explorerWindows)
            {
                var pathArr = paths.ToArray();
                var parentPidl = GetShellFolderChildrenRelativePIDL(desktop, paths.Key);
                try
                {
                    var parent = GetFolderFromPIDL(desktop, parentPidl);
                    ITEMIDLIST*[] itemArr = new ITEMIDLIST*[pathArr.Length];

                    try
                    {
                        for (int i = 0; i < pathArr.Length; i++)
                        {
                            var name = pathArr[i].Name;
                            itemArr[i] = GetShellFolderChildrenRelativePIDL(parent, name);
                        }

                        // show explorer window
                        SelectFolderItems(parentPidl, itemArr, false);
                    }
                    finally
                    {
                        foreach (var pidl in itemArr)
                        {
                            ILFree(pidl);
                        }
                    }
                }
                finally
                {
                    ILFree(parentPidl);
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

            ITEMIDLIST* item = null;
            try
            {
                var desktop = GetDesktopFolder();
                item = GetShellFolderChildrenRelativePIDL(desktop, fullPath);
                SelectFolderItems(item, null, edit);
            }
            catch
            {
                // fallback
                Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            }
            finally
            {
                ILFree(item);
            }
        }
    }
}
