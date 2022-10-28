using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using static Vanara.PInvoke.User32.WindowMessage;

namespace Clowd.Drawing
{
    internal partial class CursorResources : EmbeddedResource
    {
        private const string RSX_NS = "Clowd.Drawing.Cursors";
        private static Dictionary<string, Cursor> _cache;
        private static readonly SafeHWND _hWindow;
        private static readonly ushort _clsAtom;
        private static readonly string _clsName;
        private static readonly WindowProc _wndProc;

        private CursorResources() : base(Assembly.GetExecutingAssembly(), RSX_NS) { }

        static CursorResources()
        {
            _cache = new();
            _clsName = "ClowdCursorResources_" + DateTime.Now.Ticks;
            _wndProc = new WindowProc(ListenerWndProc);

            WNDCLASS wc;
            wc.style = 0;
            wc.lpfnWndProc = _wndProc;
            wc.cbClsExtra = 0;
            wc.cbWndExtra = 0;
            wc.hInstance = IntPtr.Zero;
            wc.hIcon = IntPtr.Zero;
            wc.hCursor = IntPtr.Zero;
            wc.hbrBackground = IntPtr.Zero;
            wc.lpszMenuName = "";
            wc.lpszClassName = _clsName;

            _clsAtom = RegisterClass(wc);
            if (_clsAtom == 0)
                throw new Win32Exception();

            _hWindow = CreateWindowEx(0, _clsName, "", 0, 0, 0, 1, 1, HWND.NULL, HMENU.NULL, HINSTANCE.NULL, IntPtr.Zero);
            if (_hWindow == IntPtr.Zero)
                throw new Win32Exception();
        }

        public static Cursor GetCursor(string fileName)
        {
            if (_cache.TryGetValue(fileName, out var cached))
                return cached;

            Cursor loaded;

            // the Cursor(Stream) constructor writes the cursor to a temporary file 
            // and then loads from file. If we can skip that, we should.
            var path = Path.Combine(AppContext.BaseDirectory, "Cursors", fileName);
            if (File.Exists(path))
            {
                loaded = new Cursor(path, true);
            }
            else
            {
                loaded = new Cursor(GetStream(RSX_NS, fileName), true);
            }

            _cache[fileName] = loaded;
            return loaded;
        }

        private static IntPtr ListenerWndProc(HWND hwnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            var msg = (WindowMessage)uMsg;
            //Debugger.Log(0, null, msg.ToString() + Environment.NewLine);

            if (msg is WM_WININICHANGE or WM_DPICHANGED or WM_DISPLAYCHANGE)
            {
                // invalidate cursor cache
                _cache.Clear();
            }

            return DefWindowProc(hwnd, uMsg, wParam, lParam);
        }
    }
}
