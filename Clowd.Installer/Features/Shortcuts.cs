using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Installer.Features
{
    public class Shortcuts : IFeature
    {
        public bool NeedsPrivileges()
        {
            return false;
        }

        public bool CheckInstalled(string assetPath)
        {
            if (ShortcutExists(Environment.SpecialFolder.StartMenu))
                return true;

            if (ShortcutExists(Environment.SpecialFolder.DesktopDirectory))
                return true;

            return false;
        }

        public void Install(string assetPath)
        {
            CreateShortcuts(assetPath, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.DesktopDirectory);
        }

        public void Uninstall(string assetPath)
        {
            DeleteShortcuts(Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.DesktopDirectory);
        }

        internal void CreateShortcuts(string assetPath, params Environment.SpecialFolder[] directories)
        {
            // if it already exists, we should just delete it so we can create a new one with updated metadata
            DeleteShortcuts(directories);

            foreach (var d in directories)
            {
                var directory = Environment.GetFolderPath(d);
                IShellLink link = (IShellLink)new ShellLink();
                link.SetDescription(Constants.ClowdAppName);
                link.SetPath(assetPath);
                link.SetWorkingDirectory(Path.GetDirectoryName(assetPath));
                link.SetIconLocation(assetPath, 0);
                IPersistFile file = (IPersistFile)link;
                file.Save(Path.Combine(directory, Constants.ShortcutName), false);
            }
        }

        internal void DeleteShortcuts(params Environment.SpecialFolder[] directories)
        {
            foreach (var d in directories)
            {
                var directory = Environment.GetFolderPath(d);
                string shortcut = Path.Combine(directory, Constants.ShortcutName);
                if (File.Exists(shortcut))
                    File.Delete(shortcut);
            }
        }

        internal bool ShortcutExists(params Environment.SpecialFolder[] directories)
        {
            foreach (var d in directories)
            {
                var directory = Environment.GetFolderPath(d);
                string shortcut = Path.Combine(directory, Constants.ShortcutName);
                if (File.Exists(shortcut))
                    return true;
            }
            return false;
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
