using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using Clowd.Config;
using Vanara.PInvoke;
using static Vanara.PInvoke.Kernel32;
using static Vanara.PInvoke.User32;

namespace Clowd.PlatformUtil.Windows
{
    public static class User32Hotkey
    {
        static WindowProc _wndProcDelegate;
        static HWND _hwnd;
        static Dictionary<int, Action> _registeredKeys = new();

        static User32Hotkey()
        {
            CreateMessageWindow();
        }

        public static IDisposable Create(GestureKey key, GestureModifierKeys modifiers, Action execute)
        {
            int virtualKeyCode = KeyInterop.VirtualKeyFromKey(key);
            var id = virtualKeyCode + ((int)modifiers * 0x10000);
            if (_registeredKeys.ContainsKey(id))
                throw new InvalidOperationException("Hot key with this key combination is already registered by this application.");

            if (!RegisterHotKey(_hwnd, id, (HotKeyModifiers)modifiers, (uint)virtualKeyCode))
                throw new Win32Exception();

            _registeredKeys.Add(id, execute);

            return Disposable.Create(() =>
            {
                UnregisterHotKey(_hwnd, id);
                _registeredKeys.Remove(id);
            });
        }

        private static void CreateMessageWindow()
        {
            // Ensure that the delegate doesn't get garbage collected by storing it as a field.
            _wndProcDelegate = new WindowProc(WndProc);

            WNDCLASSEX wndClassEx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProcDelegate,
                hInstance = GetModuleHandle(null),
                lpszClassName = "ClowdHotkeyMessageWindow_" + Guid.NewGuid(),
            };

            ushort atom = RegisterClassEx(wndClassEx);

            if (atom == 0)
                throw new Win32Exception();

            _hwnd = CreateWindowEx(0, (IntPtr)atom, null, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd.IsNull)
                throw new Win32Exception();
        }

        private static IntPtr WndProc(HWND hwnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            if (uMsg == (uint)WindowMessage.WM_HOTKEY && _registeredKeys.TryGetValue((int)wParam, out var action))
            {
                action();
            }

            return DefWindowProc(hwnd, uMsg, wParam, lParam);
        }
    }
}
