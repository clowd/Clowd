using System;
using System.Collections.Generic;
using System.Text;
using Clowd.Config;
using NativeMethods = Vanara.PInvoke.User32.VK;

namespace Clowd.PlatformUtil.Windows
{
    internal static class KeyInterop
    {
        public static GestureKey KeyFromVirtualKey(int virtualKeyInt)
        {
            GestureKey key = GestureKey.None;
            NativeMethods virtualKey = (NativeMethods)virtualKeyInt;

            switch (virtualKey)
            {
                case NativeMethods.VK_CANCEL:
                    key = GestureKey.Cancel;
                    break;

                case NativeMethods.VK_BACK:
                    key = GestureKey.Back;
                    break;

                case NativeMethods.VK_TAB:
                    key = GestureKey.Tab;
                    break;

                case NativeMethods.VK_CLEAR:
                    key = GestureKey.Clear;
                    break;

                case NativeMethods.VK_RETURN:
                    key = GestureKey.Return;
                    break;

                case NativeMethods.VK_PAUSE:
                    key = GestureKey.Pause;
                    break;

                case NativeMethods.VK_CAPITAL:
                    key = GestureKey.Capital;
                    break;

                case NativeMethods.VK_KANA:
                    key = GestureKey.KanaMode;
                    break;

                case NativeMethods.VK_JUNJA:
                    key = GestureKey.JunjaMode;
                    break;

                case NativeMethods.VK_FINAL:
                    key = GestureKey.FinalMode;
                    break;

                case NativeMethods.VK_KANJI:
                    key = GestureKey.KanjiMode;
                    break;

                case NativeMethods.VK_ESCAPE:
                    key = GestureKey.Escape;
                    break;

                case NativeMethods.VK_CONVERT:
                    key = GestureKey.ImeConvert;
                    break;

                case NativeMethods.VK_NONCONVERT:
                    key = GestureKey.ImeNonConvert;
                    break;

                case NativeMethods.VK_ACCEPT:
                    key = GestureKey.ImeAccept;
                    break;

                case NativeMethods.VK_MODECHANGE:
                    key = GestureKey.ImeModeChange;
                    break;

                case NativeMethods.VK_SPACE:
                    key = GestureKey.Space;
                    break;

                case NativeMethods.VK_PRIOR:
                    key = GestureKey.Prior;
                    break;

                case NativeMethods.VK_NEXT:
                    key = GestureKey.Next;
                    break;

                case NativeMethods.VK_END:
                    key = GestureKey.End;
                    break;

                case NativeMethods.VK_HOME:
                    key = GestureKey.Home;
                    break;

                case NativeMethods.VK_LEFT:
                    key = GestureKey.Left;
                    break;

                case NativeMethods.VK_UP:
                    key = GestureKey.Up;
                    break;

                case NativeMethods.VK_RIGHT:
                    key = GestureKey.Right;
                    break;

                case NativeMethods.VK_DOWN:
                    key = GestureKey.Down;
                    break;

                case NativeMethods.VK_SELECT:
                    key = GestureKey.Select;
                    break;

                case NativeMethods.VK_PRINT:
                    key = GestureKey.Print;
                    break;

                case NativeMethods.VK_EXECUTE:
                    key = GestureKey.Execute;
                    break;

                case NativeMethods.VK_SNAPSHOT:
                    key = GestureKey.Snapshot;
                    break;

                case NativeMethods.VK_INSERT:
                    key = GestureKey.Insert;
                    break;

                case NativeMethods.VK_DELETE:
                    key = GestureKey.Delete;
                    break;

                case NativeMethods.VK_HELP:
                    key = GestureKey.Help;
                    break;

                case NativeMethods.VK_0:
                    key = GestureKey.D0;
                    break;

                case NativeMethods.VK_1:
                    key = GestureKey.D1;
                    break;

                case NativeMethods.VK_2:
                    key = GestureKey.D2;
                    break;

                case NativeMethods.VK_3:
                    key = GestureKey.D3;
                    break;

                case NativeMethods.VK_4:
                    key = GestureKey.D4;
                    break;

                case NativeMethods.VK_5:
                    key = GestureKey.D5;
                    break;

                case NativeMethods.VK_6:
                    key = GestureKey.D6;
                    break;

                case NativeMethods.VK_7:
                    key = GestureKey.D7;
                    break;

                case NativeMethods.VK_8:
                    key = GestureKey.D8;
                    break;

                case NativeMethods.VK_9:
                    key = GestureKey.D9;
                    break;

                case NativeMethods.VK_A:
                    key = GestureKey.A;
                    break;

                case NativeMethods.VK_B:
                    key = GestureKey.B;
                    break;

                case NativeMethods.VK_C:
                    key = GestureKey.C;
                    break;

                case NativeMethods.VK_D:
                    key = GestureKey.D;
                    break;

                case NativeMethods.VK_E:
                    key = GestureKey.E;
                    break;

                case NativeMethods.VK_F:
                    key = GestureKey.F;
                    break;

                case NativeMethods.VK_G:
                    key = GestureKey.G;
                    break;

                case NativeMethods.VK_H:
                    key = GestureKey.H;
                    break;

                case NativeMethods.VK_I:
                    key = GestureKey.I;
                    break;

                case NativeMethods.VK_J:
                    key = GestureKey.J;
                    break;

                case NativeMethods.VK_K:
                    key = GestureKey.K;
                    break;

                case NativeMethods.VK_L:
                    key = GestureKey.L;
                    break;

                case NativeMethods.VK_M:
                    key = GestureKey.M;
                    break;

                case NativeMethods.VK_N:
                    key = GestureKey.N;
                    break;

                case NativeMethods.VK_O:
                    key = GestureKey.O;
                    break;

                case NativeMethods.VK_P:
                    key = GestureKey.P;
                    break;

                case NativeMethods.VK_Q:
                    key = GestureKey.Q;
                    break;

                case NativeMethods.VK_R:
                    key = GestureKey.R;
                    break;

                case NativeMethods.VK_S:
                    key = GestureKey.S;
                    break;

                case NativeMethods.VK_T:
                    key = GestureKey.T;
                    break;

                case NativeMethods.VK_U:
                    key = GestureKey.U;
                    break;

                case NativeMethods.VK_V:
                    key = GestureKey.V;
                    break;

                case NativeMethods.VK_W:
                    key = GestureKey.W;
                    break;

                case NativeMethods.VK_X:
                    key = GestureKey.X;
                    break;

                case NativeMethods.VK_Y:
                    key = GestureKey.Y;
                    break;

                case NativeMethods.VK_Z:
                    key = GestureKey.Z;
                    break;

                case NativeMethods.VK_LWIN:
                    key = GestureKey.LWin;
                    break;

                case NativeMethods.VK_RWIN:
                    key = GestureKey.RWin;
                    break;

                case NativeMethods.VK_APPS:
                    key = GestureKey.Apps;
                    break;

                case NativeMethods.VK_SLEEP:
                    key = GestureKey.Sleep;
                    break;

                case NativeMethods.VK_NUMPAD0:
                    key = GestureKey.NumPad0;
                    break;

                case NativeMethods.VK_NUMPAD1:
                    key = GestureKey.NumPad1;
                    break;

                case NativeMethods.VK_NUMPAD2:
                    key = GestureKey.NumPad2;
                    break;

                case NativeMethods.VK_NUMPAD3:
                    key = GestureKey.NumPad3;
                    break;

                case NativeMethods.VK_NUMPAD4:
                    key = GestureKey.NumPad4;
                    break;

                case NativeMethods.VK_NUMPAD5:
                    key = GestureKey.NumPad5;
                    break;

                case NativeMethods.VK_NUMPAD6:
                    key = GestureKey.NumPad6;
                    break;

                case NativeMethods.VK_NUMPAD7:
                    key = GestureKey.NumPad7;
                    break;

                case NativeMethods.VK_NUMPAD8:
                    key = GestureKey.NumPad8;
                    break;

                case NativeMethods.VK_NUMPAD9:
                    key = GestureKey.NumPad9;
                    break;

                case NativeMethods.VK_MULTIPLY:
                    key = GestureKey.Multiply;
                    break;

                case NativeMethods.VK_ADD:
                    key = GestureKey.Add;
                    break;

                case NativeMethods.VK_SEPARATOR:
                    key = GestureKey.Separator;
                    break;

                case NativeMethods.VK_SUBTRACT:
                    key = GestureKey.Subtract;
                    break;

                case NativeMethods.VK_DECIMAL:
                    key = GestureKey.Decimal;
                    break;

                case NativeMethods.VK_DIVIDE:
                    key = GestureKey.Divide;
                    break;

                case NativeMethods.VK_F1:
                    key = GestureKey.F1;
                    break;

                case NativeMethods.VK_F2:
                    key = GestureKey.F2;
                    break;

                case NativeMethods.VK_F3:
                    key = GestureKey.F3;
                    break;

                case NativeMethods.VK_F4:
                    key = GestureKey.F4;
                    break;

                case NativeMethods.VK_F5:
                    key = GestureKey.F5;
                    break;

                case NativeMethods.VK_F6:
                    key = GestureKey.F6;
                    break;

                case NativeMethods.VK_F7:
                    key = GestureKey.F7;
                    break;

                case NativeMethods.VK_F8:
                    key = GestureKey.F8;
                    break;

                case NativeMethods.VK_F9:
                    key = GestureKey.F9;
                    break;

                case NativeMethods.VK_F10:
                    key = GestureKey.F10;
                    break;

                case NativeMethods.VK_F11:
                    key = GestureKey.F11;
                    break;

                case NativeMethods.VK_F12:
                    key = GestureKey.F12;
                    break;

                case NativeMethods.VK_F13:
                    key = GestureKey.F13;
                    break;

                case NativeMethods.VK_F14:
                    key = GestureKey.F14;
                    break;

                case NativeMethods.VK_F15:
                    key = GestureKey.F15;
                    break;

                case NativeMethods.VK_F16:
                    key = GestureKey.F16;
                    break;

                case NativeMethods.VK_F17:
                    key = GestureKey.F17;
                    break;

                case NativeMethods.VK_F18:
                    key = GestureKey.F18;
                    break;

                case NativeMethods.VK_F19:
                    key = GestureKey.F19;
                    break;

                case NativeMethods.VK_F20:
                    key = GestureKey.F20;
                    break;

                case NativeMethods.VK_F21:
                    key = GestureKey.F21;
                    break;

                case NativeMethods.VK_F22:
                    key = GestureKey.F22;
                    break;

                case NativeMethods.VK_F23:
                    key = GestureKey.F23;
                    break;

                case NativeMethods.VK_F24:
                    key = GestureKey.F24;
                    break;

                case NativeMethods.VK_NUMLOCK:
                    key = GestureKey.NumLock;
                    break;

                case NativeMethods.VK_SCROLL:
                    key = GestureKey.Scroll;
                    break;

                case NativeMethods.VK_SHIFT:
                case NativeMethods.VK_LSHIFT:
                    key = GestureKey.LeftShift;
                    break;

                case NativeMethods.VK_RSHIFT:
                    key = GestureKey.RightShift;
                    break;

                case NativeMethods.VK_CONTROL:
                case NativeMethods.VK_LCONTROL:
                    key = GestureKey.LeftCtrl;
                    break;

                case NativeMethods.VK_RCONTROL:
                    key = GestureKey.RightCtrl;
                    break;

                case NativeMethods.VK_MENU:
                case NativeMethods.VK_LMENU:
                    key = GestureKey.LeftAlt;
                    break;

                case NativeMethods.VK_RMENU:
                    key = GestureKey.RightAlt;
                    break;

                case NativeMethods.VK_BROWSER_BACK:
                    key = GestureKey.BrowserBack;
                    break;

                case NativeMethods.VK_BROWSER_FORWARD:
                    key = GestureKey.BrowserForward;
                    break;

                case NativeMethods.VK_BROWSER_REFRESH:
                    key = GestureKey.BrowserRefresh;
                    break;

                case NativeMethods.VK_BROWSER_STOP:
                    key = GestureKey.BrowserStop;
                    break;

                case NativeMethods.VK_BROWSER_SEARCH:
                    key = GestureKey.BrowserSearch;
                    break;

                case NativeMethods.VK_BROWSER_FAVORITES:
                    key = GestureKey.BrowserFavorites;
                    break;

                case NativeMethods.VK_BROWSER_HOME:
                    key = GestureKey.BrowserHome;
                    break;

                case NativeMethods.VK_VOLUME_MUTE:
                    key = GestureKey.VolumeMute;
                    break;

                case NativeMethods.VK_VOLUME_DOWN:
                    key = GestureKey.VolumeDown;
                    break;

                case NativeMethods.VK_VOLUME_UP:
                    key = GestureKey.VolumeUp;
                    break;

                case NativeMethods.VK_MEDIA_NEXT_TRACK:
                    key = GestureKey.MediaNextTrack;
                    break;

                case NativeMethods.VK_MEDIA_PREV_TRACK:
                    key = GestureKey.MediaPreviousTrack;
                    break;

                case NativeMethods.VK_MEDIA_STOP:
                    key = GestureKey.MediaStop;
                    break;

                case NativeMethods.VK_MEDIA_PLAY_PAUSE:
                    key = GestureKey.MediaPlayPause;
                    break;

                case NativeMethods.VK_LAUNCH_MAIL:
                    key = GestureKey.LaunchMail;
                    break;

                case NativeMethods.VK_LAUNCH_MEDIA_SELECT:
                    key = GestureKey.SelectMedia;
                    break;

                case NativeMethods.VK_LAUNCH_APP1:
                    key = GestureKey.LaunchApplication1;
                    break;

                case NativeMethods.VK_LAUNCH_APP2:
                    key = GestureKey.LaunchApplication2;
                    break;

                case NativeMethods.VK_OEM_1:
                    key = GestureKey.OemSemicolon;
                    break;

                case NativeMethods.VK_OEM_PLUS:
                    key = GestureKey.OemPlus;
                    break;

                case NativeMethods.VK_OEM_COMMA:
                    key = GestureKey.OemComma;
                    break;

                case NativeMethods.VK_OEM_MINUS:
                    key = GestureKey.OemMinus;
                    break;

                case NativeMethods.VK_OEM_PERIOD:
                    key = GestureKey.OemPeriod;
                    break;

                case NativeMethods.VK_OEM_2:
                    key = GestureKey.OemQuestion;
                    break;

                case NativeMethods.VK_OEM_3:
                    key = GestureKey.OemTilde;
                    break;

                //case NativeMethods.VK_C1:
                //    key = GestureKey.AbntC1;
                //    break;

                //case NativeMethods.VK_C2:
                //    key = GestureKey.AbntC2;
                //    break;

                case NativeMethods.VK_OEM_4:
                    key = GestureKey.OemOpenBrackets;
                    break;

                case NativeMethods.VK_OEM_5:
                    key = GestureKey.OemPipe;
                    break;

                case NativeMethods.VK_OEM_6:
                    key = GestureKey.OemCloseBrackets;
                    break;

                case NativeMethods.VK_OEM_7:
                    key = GestureKey.OemQuotes;
                    break;

                case NativeMethods.VK_OEM_8:
                    key = GestureKey.Oem8;
                    break;

                case NativeMethods.VK_OEM_102:
                    key = GestureKey.OemBackslash;
                    break;

                case NativeMethods.VK_PROCESSKEY:
                    key = GestureKey.ImeProcessed;
                    break;

                case NativeMethods.VK_OEM_ATTN: // VK_DBE_ALPHANUMERIC
                    key = GestureKey.OemAttn;          // DbeAlphanumeric
                    break;

                case NativeMethods.VK_OEM_FINISH: // VK_DBE_KATAKANA
                    key = GestureKey.OemFinish;          // DbeKatakana
                    break;

                case NativeMethods.VK_OEM_COPY: // VK_DBE_HIRAGANA
                    key = GestureKey.OemCopy;          // DbeHiragana
                    break;

                case NativeMethods.VK_OEM_AUTO: // VK_DBE_SBCSCHAR
                    key = GestureKey.OemAuto;          // DbeSbcsChar
                    break;

                case NativeMethods.VK_OEM_ENLW: // VK_DBE_DBCSCHAR
                    key = GestureKey.OemEnlw;          // DbeDbcsChar
                    break;

                case NativeMethods.VK_OEM_BACKTAB: // VK_DBE_ROMAN
                    key = GestureKey.OemBackTab;          // DbeRoman
                    break;

                case NativeMethods.VK_ATTN: // VK_DBE_NOROMAN
                    key = GestureKey.Attn;         // DbeNoRoman
                    break;

                case NativeMethods.VK_CRSEL: // VK_DBE_ENTERWORDREGISTERMODE
                    key = GestureKey.CrSel;         // DbeEnterWordRegisterMode
                    break;

                case NativeMethods.VK_EXSEL: // VK_DBE_ENTERIMECONFIGMODE
                    key = GestureKey.ExSel;         // DbeEnterImeConfigMode
                    break;

                case NativeMethods.VK_EREOF: // VK_DBE_FLUSHSTRING
                    key = GestureKey.EraseEof;      // DbeFlushString
                    break;

                case NativeMethods.VK_PLAY: // VK_DBE_CODEINPUT
                    key = GestureKey.Play;         // DbeCodeInput
                    break;

                case NativeMethods.VK_ZOOM: // VK_DBE_NOCODEINPUT
                    key = GestureKey.Zoom;         // DbeNoCodeInput
                    break;

                case NativeMethods.VK_NONAME: // VK_DBE_DETERMINESTRING
                    key = GestureKey.NoName;         // DbeDetermineString
                    break;

                case NativeMethods.VK_PA1: // VK_DBE_ENTERDLGCONVERSIONMODE
                    key = GestureKey.Pa1;         // DbeEnterDlgConversionMode
                    break;

                case NativeMethods.VK_OEM_CLEAR:
                    key = GestureKey.OemClear;
                    break;

                default:
                    key = GestureKey.None;
                    break;
            }

            return key;
        }

        /// <summary>
        ///     Convert our Key enum into a Win32 VirtualGestureKey.
        /// </summary>
        public static int VirtualKeyFromKey(GestureKey key)
        {
            NativeMethods virtualKey = 0;

            switch (key)
            {
                case GestureKey.Cancel:
                    virtualKey = NativeMethods.VK_CANCEL;
                    break;

                case GestureKey.Back:
                    virtualKey = NativeMethods.VK_BACK;
                    break;

                case GestureKey.Tab:
                    virtualKey = NativeMethods.VK_TAB;
                    break;

                case GestureKey.Clear:
                    virtualKey = NativeMethods.VK_CLEAR;
                    break;

                case GestureKey.Return:
                    virtualKey = NativeMethods.VK_RETURN;
                    break;

                case GestureKey.Pause:
                    virtualKey = NativeMethods.VK_PAUSE;
                    break;

                case GestureKey.Capital:
                    virtualKey = NativeMethods.VK_CAPITAL;
                    break;

                case GestureKey.KanaMode:
                    virtualKey = NativeMethods.VK_KANA;
                    break;

                case GestureKey.JunjaMode:
                    virtualKey = NativeMethods.VK_JUNJA;
                    break;

                case GestureKey.FinalMode:
                    virtualKey = NativeMethods.VK_FINAL;
                    break;

                case GestureKey.KanjiMode:
                    virtualKey = NativeMethods.VK_KANJI;
                    break;

                case GestureKey.Escape:
                    virtualKey = NativeMethods.VK_ESCAPE;
                    break;

                case GestureKey.ImeConvert:
                    virtualKey = NativeMethods.VK_CONVERT;
                    break;

                case GestureKey.ImeNonConvert:
                    virtualKey = NativeMethods.VK_NONCONVERT;
                    break;

                case GestureKey.ImeAccept:
                    virtualKey = NativeMethods.VK_ACCEPT;
                    break;

                case GestureKey.ImeModeChange:
                    virtualKey = NativeMethods.VK_MODECHANGE;
                    break;

                case GestureKey.Space:
                    virtualKey = NativeMethods.VK_SPACE;
                    break;

                case GestureKey.Prior:
                    virtualKey = NativeMethods.VK_PRIOR;
                    break;

                case GestureKey.Next:
                    virtualKey = NativeMethods.VK_NEXT;
                    break;

                case GestureKey.End:
                    virtualKey = NativeMethods.VK_END;
                    break;

                case GestureKey.Home:
                    virtualKey = NativeMethods.VK_HOME;
                    break;

                case GestureKey.Left:
                    virtualKey = NativeMethods.VK_LEFT;
                    break;

                case GestureKey.Up:
                    virtualKey = NativeMethods.VK_UP;
                    break;

                case GestureKey.Right:
                    virtualKey = NativeMethods.VK_RIGHT;
                    break;

                case GestureKey.Down:
                    virtualKey = NativeMethods.VK_DOWN;
                    break;

                case GestureKey.Select:
                    virtualKey = NativeMethods.VK_SELECT;
                    break;

                case GestureKey.Print:
                    virtualKey = NativeMethods.VK_PRINT;
                    break;

                case GestureKey.Execute:
                    virtualKey = NativeMethods.VK_EXECUTE;
                    break;

                case GestureKey.Snapshot:
                    virtualKey = NativeMethods.VK_SNAPSHOT;
                    break;

                case GestureKey.Insert:
                    virtualKey = NativeMethods.VK_INSERT;
                    break;

                case GestureKey.Delete:
                    virtualKey = NativeMethods.VK_DELETE;
                    break;

                case GestureKey.Help:
                    virtualKey = NativeMethods.VK_HELP;
                    break;

                case GestureKey.D0:
                    virtualKey = NativeMethods.VK_0;
                    break;

                case GestureKey.D1:
                    virtualKey = NativeMethods.VK_1;
                    break;

                case GestureKey.D2:
                    virtualKey = NativeMethods.VK_2;
                    break;

                case GestureKey.D3:
                    virtualKey = NativeMethods.VK_3;
                    break;

                case GestureKey.D4:
                    virtualKey = NativeMethods.VK_4;
                    break;

                case GestureKey.D5:
                    virtualKey = NativeMethods.VK_5;
                    break;

                case GestureKey.D6:
                    virtualKey = NativeMethods.VK_6;
                    break;

                case GestureKey.D7:
                    virtualKey = NativeMethods.VK_7;
                    break;

                case GestureKey.D8:
                    virtualKey = NativeMethods.VK_8;
                    break;

                case GestureKey.D9:
                    virtualKey = NativeMethods.VK_9;
                    break;

                case GestureKey.A:
                    virtualKey = NativeMethods.VK_A;
                    break;

                case GestureKey.B:
                    virtualKey = NativeMethods.VK_B;
                    break;

                case GestureKey.C:
                    virtualKey = NativeMethods.VK_C;
                    break;

                case GestureKey.D:
                    virtualKey = NativeMethods.VK_D;
                    break;

                case GestureKey.E:
                    virtualKey = NativeMethods.VK_E;
                    break;

                case GestureKey.F:
                    virtualKey = NativeMethods.VK_F;
                    break;

                case GestureKey.G:
                    virtualKey = NativeMethods.VK_G;
                    break;

                case GestureKey.H:
                    virtualKey = NativeMethods.VK_H;
                    break;

                case GestureKey.I:
                    virtualKey = NativeMethods.VK_I;
                    break;

                case GestureKey.J:
                    virtualKey = NativeMethods.VK_J;
                    break;

                case GestureKey.K:
                    virtualKey = NativeMethods.VK_K;
                    break;

                case GestureKey.L:
                    virtualKey = NativeMethods.VK_L;
                    break;

                case GestureKey.M:
                    virtualKey = NativeMethods.VK_M;
                    break;

                case GestureKey.N:
                    virtualKey = NativeMethods.VK_N;
                    break;

                case GestureKey.O:
                    virtualKey = NativeMethods.VK_O;
                    break;

                case GestureKey.P:
                    virtualKey = NativeMethods.VK_P;
                    break;

                case GestureKey.Q:
                    virtualKey = NativeMethods.VK_Q;
                    break;

                case GestureKey.R:
                    virtualKey = NativeMethods.VK_R;
                    break;

                case GestureKey.S:
                    virtualKey = NativeMethods.VK_S;
                    break;

                case GestureKey.T:
                    virtualKey = NativeMethods.VK_T;
                    break;

                case GestureKey.U:
                    virtualKey = NativeMethods.VK_U;
                    break;

                case GestureKey.V:
                    virtualKey = NativeMethods.VK_V;
                    break;

                case GestureKey.W:
                    virtualKey = NativeMethods.VK_W;
                    break;

                case GestureKey.X:
                    virtualKey = NativeMethods.VK_X;
                    break;

                case GestureKey.Y:
                    virtualKey = NativeMethods.VK_Y;
                    break;

                case GestureKey.Z:
                    virtualKey = NativeMethods.VK_Z;
                    break;

                case GestureKey.LWin:
                    virtualKey = NativeMethods.VK_LWIN;
                    break;

                case GestureKey.RWin:
                    virtualKey = NativeMethods.VK_RWIN;
                    break;

                case GestureKey.Apps:
                    virtualKey = NativeMethods.VK_APPS;
                    break;

                case GestureKey.Sleep:
                    virtualKey = NativeMethods.VK_SLEEP;
                    break;

                case GestureKey.NumPad0:
                    virtualKey = NativeMethods.VK_NUMPAD0;
                    break;

                case GestureKey.NumPad1:
                    virtualKey = NativeMethods.VK_NUMPAD1;
                    break;

                case GestureKey.NumPad2:
                    virtualKey = NativeMethods.VK_NUMPAD2;
                    break;

                case GestureKey.NumPad3:
                    virtualKey = NativeMethods.VK_NUMPAD3;
                    break;

                case GestureKey.NumPad4:
                    virtualKey = NativeMethods.VK_NUMPAD4;
                    break;

                case GestureKey.NumPad5:
                    virtualKey = NativeMethods.VK_NUMPAD5;
                    break;

                case GestureKey.NumPad6:
                    virtualKey = NativeMethods.VK_NUMPAD6;
                    break;

                case GestureKey.NumPad7:
                    virtualKey = NativeMethods.VK_NUMPAD7;
                    break;

                case GestureKey.NumPad8:
                    virtualKey = NativeMethods.VK_NUMPAD8;
                    break;

                case GestureKey.NumPad9:
                    virtualKey = NativeMethods.VK_NUMPAD9;
                    break;

                case GestureKey.Multiply:
                    virtualKey = NativeMethods.VK_MULTIPLY;
                    break;

                case GestureKey.Add:
                    virtualKey = NativeMethods.VK_ADD;
                    break;

                case GestureKey.Separator:
                    virtualKey = NativeMethods.VK_SEPARATOR;
                    break;

                case GestureKey.Subtract:
                    virtualKey = NativeMethods.VK_SUBTRACT;
                    break;

                case GestureKey.Decimal:
                    virtualKey = NativeMethods.VK_DECIMAL;
                    break;

                case GestureKey.Divide:
                    virtualKey = NativeMethods.VK_DIVIDE;
                    break;

                case GestureKey.F1:
                    virtualKey = NativeMethods.VK_F1;
                    break;

                case GestureKey.F2:
                    virtualKey = NativeMethods.VK_F2;
                    break;

                case GestureKey.F3:
                    virtualKey = NativeMethods.VK_F3;
                    break;

                case GestureKey.F4:
                    virtualKey = NativeMethods.VK_F4;
                    break;

                case GestureKey.F5:
                    virtualKey = NativeMethods.VK_F5;
                    break;

                case GestureKey.F6:
                    virtualKey = NativeMethods.VK_F6;
                    break;

                case GestureKey.F7:
                    virtualKey = NativeMethods.VK_F7;
                    break;

                case GestureKey.F8:
                    virtualKey = NativeMethods.VK_F8;
                    break;

                case GestureKey.F9:
                    virtualKey = NativeMethods.VK_F9;
                    break;

                case GestureKey.F10:
                    virtualKey = NativeMethods.VK_F10;
                    break;

                case GestureKey.F11:
                    virtualKey = NativeMethods.VK_F11;
                    break;

                case GestureKey.F12:
                    virtualKey = NativeMethods.VK_F12;
                    break;

                case GestureKey.F13:
                    virtualKey = NativeMethods.VK_F13;
                    break;

                case GestureKey.F14:
                    virtualKey = NativeMethods.VK_F14;
                    break;

                case GestureKey.F15:
                    virtualKey = NativeMethods.VK_F15;
                    break;

                case GestureKey.F16:
                    virtualKey = NativeMethods.VK_F16;
                    break;

                case GestureKey.F17:
                    virtualKey = NativeMethods.VK_F17;
                    break;

                case GestureKey.F18:
                    virtualKey = NativeMethods.VK_F18;
                    break;

                case GestureKey.F19:
                    virtualKey = NativeMethods.VK_F19;
                    break;

                case GestureKey.F20:
                    virtualKey = NativeMethods.VK_F20;
                    break;

                case GestureKey.F21:
                    virtualKey = NativeMethods.VK_F21;
                    break;

                case GestureKey.F22:
                    virtualKey = NativeMethods.VK_F22;
                    break;

                case GestureKey.F23:
                    virtualKey = NativeMethods.VK_F23;
                    break;

                case GestureKey.F24:
                    virtualKey = NativeMethods.VK_F24;
                    break;

                case GestureKey.NumLock:
                    virtualKey = NativeMethods.VK_NUMLOCK;
                    break;

                case GestureKey.Scroll:
                    virtualKey = NativeMethods.VK_SCROLL;
                    break;

                case GestureKey.LeftShift:
                    virtualKey = NativeMethods.VK_LSHIFT;
                    break;

                case GestureKey.RightShift:
                    virtualKey = NativeMethods.VK_RSHIFT;
                    break;

                case GestureKey.LeftCtrl:
                    virtualKey = NativeMethods.VK_LCONTROL;
                    break;

                case GestureKey.RightCtrl:
                    virtualKey = NativeMethods.VK_RCONTROL;
                    break;

                case GestureKey.LeftAlt:
                    virtualKey = NativeMethods.VK_LMENU;
                    break;

                case GestureKey.RightAlt:
                    virtualKey = NativeMethods.VK_RMENU;
                    break;

                case GestureKey.BrowserBack:
                    virtualKey = NativeMethods.VK_BROWSER_BACK;
                    break;

                case GestureKey.BrowserForward:
                    virtualKey = NativeMethods.VK_BROWSER_FORWARD;
                    break;

                case GestureKey.BrowserRefresh:
                    virtualKey = NativeMethods.VK_BROWSER_REFRESH;
                    break;

                case GestureKey.BrowserStop:
                    virtualKey = NativeMethods.VK_BROWSER_STOP;
                    break;

                case GestureKey.BrowserSearch:
                    virtualKey = NativeMethods.VK_BROWSER_SEARCH;
                    break;

                case GestureKey.BrowserFavorites:
                    virtualKey = NativeMethods.VK_BROWSER_FAVORITES;
                    break;

                case GestureKey.BrowserHome:
                    virtualKey = NativeMethods.VK_BROWSER_HOME;
                    break;

                case GestureKey.VolumeMute:
                    virtualKey = NativeMethods.VK_VOLUME_MUTE;
                    break;

                case GestureKey.VolumeDown:
                    virtualKey = NativeMethods.VK_VOLUME_DOWN;
                    break;

                case GestureKey.VolumeUp:
                    virtualKey = NativeMethods.VK_VOLUME_UP;
                    break;

                case GestureKey.MediaNextTrack:
                    virtualKey = NativeMethods.VK_MEDIA_NEXT_TRACK;
                    break;

                case GestureKey.MediaPreviousTrack:
                    virtualKey = NativeMethods.VK_MEDIA_PREV_TRACK;
                    break;

                case GestureKey.MediaStop:
                    virtualKey = NativeMethods.VK_MEDIA_STOP;
                    break;

                case GestureKey.MediaPlayPause:
                    virtualKey = NativeMethods.VK_MEDIA_PLAY_PAUSE;
                    break;

                case GestureKey.LaunchMail:
                    virtualKey = NativeMethods.VK_LAUNCH_MAIL;
                    break;

                case GestureKey.SelectMedia:
                    virtualKey = NativeMethods.VK_LAUNCH_MEDIA_SELECT;
                    break;

                case GestureKey.LaunchApplication1:
                    virtualKey = NativeMethods.VK_LAUNCH_APP1;
                    break;

                case GestureKey.LaunchApplication2:
                    virtualKey = NativeMethods.VK_LAUNCH_APP2;
                    break;

                case GestureKey.OemSemicolon:
                    virtualKey = NativeMethods.VK_OEM_1;
                    break;

                case GestureKey.OemPlus:
                    virtualKey = NativeMethods.VK_OEM_PLUS;
                    break;

                case GestureKey.OemComma:
                    virtualKey = NativeMethods.VK_OEM_COMMA;
                    break;

                case GestureKey.OemMinus:
                    virtualKey = NativeMethods.VK_OEM_MINUS;
                    break;

                case GestureKey.OemPeriod:
                    virtualKey = NativeMethods.VK_OEM_PERIOD;
                    break;

                case GestureKey.OemQuestion:
                    virtualKey = NativeMethods.VK_OEM_2;
                    break;

                case GestureKey.OemTilde:
                    virtualKey = NativeMethods.VK_OEM_3;
                    break;

                //case GestureKey.AbntC1:
                //    virtualKey = NativeMethods.VK_C1;
                //    break;

                //case GestureKey.AbntC2:
                //    virtualKey = NativeMethods.VK_C2;
                //    break;

                case GestureKey.OemOpenBrackets:
                    virtualKey = NativeMethods.VK_OEM_4;
                    break;

                case GestureKey.OemPipe:
                    virtualKey = NativeMethods.VK_OEM_5;
                    break;

                case GestureKey.OemCloseBrackets:
                    virtualKey = NativeMethods.VK_OEM_6;
                    break;

                case GestureKey.OemQuotes:
                    virtualKey = NativeMethods.VK_OEM_7;
                    break;

                case GestureKey.Oem8:
                    virtualKey = NativeMethods.VK_OEM_8;
                    break;

                case GestureKey.OemBackslash:
                    virtualKey = NativeMethods.VK_OEM_102;
                    break;

                case GestureKey.ImeProcessed:
                    virtualKey = NativeMethods.VK_PROCESSKEY;
                    break;

                case GestureKey.OemAttn:                           // DbeAlphanumeric
                    virtualKey = NativeMethods.VK_OEM_ATTN; // VK_DBE_ALPHANUMERIC
                    break;

                case GestureKey.OemFinish:                           // DbeKatakana
                    virtualKey = NativeMethods.VK_OEM_FINISH; // VK_DBE_KATAKANA
                    break;

                case GestureKey.OemCopy:                           // DbeHiragana
                    virtualKey = NativeMethods.VK_OEM_COPY; // VK_DBE_HIRAGANA
                    break;

                case GestureKey.OemAuto:                           // DbeSbcsChar
                    virtualKey = NativeMethods.VK_OEM_AUTO; // VK_DBE_SBCSCHAR
                    break;

                case GestureKey.OemEnlw:                           // DbeDbcsChar
                    virtualKey = NativeMethods.VK_OEM_ENLW; // VK_DBE_DBCSCHAR
                    break;

                case GestureKey.OemBackTab:                           // DbeRoman
                    virtualKey = NativeMethods.VK_OEM_BACKTAB; // VK_DBE_ROMAN
                    break;

                case GestureKey.Attn:                          // DbeNoRoman
                    virtualKey = NativeMethods.VK_ATTN; // VK_DBE_NOROMAN
                    break;

                case GestureKey.CrSel:                          // DbeEnterWordRegisterMode
                    virtualKey = NativeMethods.VK_CRSEL; // VK_DBE_ENTERWORDREGISTERMODE
                    break;

                case GestureKey.ExSel:                          // EnterImeConfigureMode
                    virtualKey = NativeMethods.VK_EXSEL; // VK_DBE_ENTERIMECONFIGMODE
                    break;

                case GestureKey.EraseEof:                       // DbeFlushString
                    virtualKey = NativeMethods.VK_EREOF; // VK_DBE_FLUSHSTRING
                    break;

                case GestureKey.Play:                           // DbeCodeInput
                    virtualKey = NativeMethods.VK_PLAY;  // VK_DBE_CODEINPUT
                    break;

                case GestureKey.Zoom:                           // DbeNoCodeInput
                    virtualKey = NativeMethods.VK_ZOOM;  // VK_DBE_NOCODEINPUT
                    break;

                case GestureKey.NoName:                          // DbeDetermineString
                    virtualKey = NativeMethods.VK_NONAME; // VK_DBE_DETERMINESTRING
                    break;

                case GestureKey.Pa1:                          // DbeEnterDlgConversionMode
                    virtualKey = NativeMethods.VK_PA1; // VK_ENTERDLGCONVERSIONMODE
                    break;

                case GestureKey.OemClear:
                    virtualKey = NativeMethods.VK_OEM_CLEAR;
                    break;

                case GestureKey.DeadCharProcessed:             //This is usused.  It's just here for completeness.
                    virtualKey = 0;                     //There is no Win32 VKey for this.
                    break;

                default:
                    virtualKey = 0;
                    break;
            }

            return (int)virtualKey;
        }
    }
}
