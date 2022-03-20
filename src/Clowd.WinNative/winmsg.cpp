#pragma once
#include "pch.h"
#include "winmsg.h"

using namespace std;

//void ShowMessageText(UINT msg)
//{
//    wchar_t str[1024];
//    wchar_t* win_msgtext = (wchar_t*)GetMessageText(msg);
//    if (win_msgtext)
//    {
//        //printf(L"WndProc: msg = %x (%s)\n", msg, win_msgtext);
//        wsprintf(str, L"WndProc: msg = %x (%s)\n", msg, win_msgtext);
//        OutputDebugString(str);
//    }
//}
//
//To list messages used(and generate an 'ignore' list) call:
//ShowUsedMessages();
//
//
//// win_msg.h file ----------
//#define SHOW_USED_MESSAGES 0

//wchar_t* GetMessageText(unsigned int msg);
//
//#ifdef SHOW_USED_MESSAGES
//void ShowUsedMessages(void);
//#endif

// win_msg.cpp -----------
//#include "pch.h"
//#include "win_msg.h"

// List here messages to ignore (-1 signifies end of list)
// if -999 occurs at the start of the list, ALL messages except these are ignored (ie. inverts)
//int msgs_to_ignore[] = { -1 };
//int msgs_to_ignore[] = {//-999,
//  0x20, 0x84, 0xA0, 0x113, 0x200
//};
// 0x0020 - WM_SETCURSOR (45)
// 0x0084 - WM_NCHITTEST (26)
// 0x0113 - WM_TIMER (46)
// 0x0135 - WM_CTLCOLORBTN (8)
// 0x0200 - WM_MOUSEFIRST (26)

typedef struct {
	unsigned long code;
	wchar_t* text;
} XMSGITEM;

// These from https://wiki.winehq.org/List_Of_Windows_Messages
XMSGITEM xmsglist[] =
{
  { 0, L"WM_NULL"},
  { 1, L"WM_CREATE" },
  { 2, L"WM_DESTROY" },
  { 3, L"WM_MOVE" },
  { 5, L"WM_SIZE" },
  { 6, L"WM_ACTIVATE" },
  { 7, L"WM_SETFOCUS" },
  { 8, L"WM_KILLFOCUS" },
  { 10, L"WM_ENABLE" },
  { 11, L"WM_SETREDRAW" },
  { 12, L"WM_SETTEXT" },
  { 13, L"WM_GETTEXT" },
  { 14, L"WM_GETTEXTLENGTH" },
  { 15, L"WM_PAINT" },
  { 16, L"WM_CLOSE" },
  { 17, L"WM_QUERYENDSESSION" },
  { 18, L"WM_QUIT" },
  { 19, L"WM_QUERYOPEN" },
  { 20, L"WM_ERASEBKGND" },
  { 21, L"WM_SYSCOLORCHANGE" },
  { 22, L"WM_ENDSESSION" },
  { 24, L"WM_SHOWWINDOW" },
  { 25, L"WM_CTLCOLOR" },
  { 26, L"WM_WININICHANGE" },
  { 27, L"WM_DEVMODECHANGE" },
  { 28, L"WM_ACTIVATEAPP" },
  { 29, L"WM_FONTCHANGE" },
  { 30, L"WM_TIMECHANGE" },
  { 31, L"WM_CANCELMODE" },
  { 32, L"WM_SETCURSOR" },
  { 33, L"WM_MOUSEACTIVATE" },
  { 34, L"WM_CHILDACTIVATE" },
  { 35, L"WM_QUEUESYNC" },
  { 36, L"WM_GETMINMAXINFO" },
  { 38, L"WM_PAINTICON" },
  { 39, L"WM_ICONERASEBKGND" },
  { 40, L"WM_NEXTDLGCTL" },
  { 42, L"WM_SPOOLERSTATUS" },
  { 43, L"WM_DRAWITEM" },
  { 44, L"WM_MEASUREITEM" },
  { 45, L"WM_DELETEITEM" },
  { 46, L"WM_VKEYTOITEM" },
  { 47, L"WM_CHARTOITEM" },
  { 48, L"WM_SETFONT" },
  { 49, L"WM_GETFONT" },
  { 50, L"WM_SETHOTKEY" },
  { 51, L"WM_GETHOTKEY" },
  { 55, L"WM_QUERYDRAGICON" },
  { 57, L"WM_COMPAREITEM" },
  { 61, L"WM_GETOBJECT" },
  { 65, L"WM_COMPACTING" },
  { 68, L"WM_COMMNOTIFY" },
  { 70, L"WM_WINDOWPOSCHANGING" },
  { 71, L"WM_WINDOWPOSCHANGED" },
  { 72, L"WM_POWER" },
  { 73, L"WM_COPYGLOBALDATA" },
  { 74, L"WM_COPYDATA" },
  { 75, L"WM_CANCELJOURNAL" },
  { 78, L"WM_NOTIFY" },
  { 80, L"WM_INPUTLANGCHANGEREQUEST" },
  { 81, L"WM_INPUTLANGCHANGE" },
  { 82, L"WM_TCARD" },
  { 83, L"WM_HELP" },
  { 84, L"WM_USERCHANGED" },
  { 85, L"WM_NOTIFYFORMAT" },
  { 123, L"WM_CONTEXTMENU" },
  { 124, L"WM_STYLECHANGING" },
  { 125, L"WM_STYLECHANGED" },
  { 126, L"WM_DISPLAYCHANGE" },
  { 127, L"WM_GETICON" },
  { 128, L"WM_SETICON" },
  { 129, L"WM_NCCREATE" },
  { 130, L"WM_NCDESTROY" },
  { 131, L"WM_NCCALCSIZE" },
  { 132, L"WM_NCHITTEST" },
  { 133, L"WM_NCPAINT" },
  { 134, L"WM_NCACTIVATE" },
  { 135, L"WM_GETDLGCODE" },
  { 136, L"WM_SYNCPAINT" },
  { 160, L"WM_NCMOUSEMOVE" },
  { 161, L"WM_NCLBUTTONDOWN" },
  { 162, L"WM_NCLBUTTONUP" },
  { 163, L"WM_NCLBUTTONDBLCLK" },
  { 164, L"WM_NCRBUTTONDOWN" },
  { 165, L"WM_NCRBUTTONUP" },
  { 166, L"WM_NCRBUTTONDBLCLK" },
  { 167, L"WM_NCMBUTTONDOWN" },
  { 168, L"WM_NCMBUTTONUP" },
  { 169, L"WM_NCMBUTTONDBLCLK" },
  { 171, L"WM_NCXBUTTONDOWN" },
  { 172, L"WM_NCXBUTTONUP" },
  { 173, L"WM_NCXBUTTONDBLCLK" },
  { 176, L"EM_GETSEL" },
  { 177, L"EM_SETSEL" },
  { 178, L"EM_GETRECT" },
  { 179, L"EM_SETRECT" },
  { 180, L"EM_SETRECTNP" },
  { 181, L"EM_SCROLL" },
  { 182, L"EM_LINESCROLL" },
  { 183, L"EM_SCROLLCARET" },
  { 185, L"EM_GETMODIFY" },
  { 187, L"EM_SETMODIFY" },
  { 188, L"EM_GETLINECOUNT" },
  { 189, L"EM_LINEINDEX" },
  { 190, L"EM_SETHANDLE" },
  { 191, L"EM_GETHANDLE" },
  { 192, L"EM_GETTHUMB" },
  { 193, L"EM_LINELENGTH" },
  { 194, L"EM_REPLACESEL" },
  { 195, L"EM_SETFONT" },
  { 196, L"EM_GETLINE" },
  { 197, L"EM_LIMITTEXT" },
  { 197, L"EM_SETLIMITTEXT" },
  { 198, L"EM_CANUNDO" },
  { 199, L"EM_UNDO" },
  { 200, L"EM_FMTLINES" },
  { 201, L"EM_LINEFROMCHAR" },
  { 202, L"EM_SETWORDBREAK" },
  { 203, L"EM_SETTABSTOPS" },
  { 204, L"EM_SETPASSWORDCHAR" },
  { 205, L"EM_EMPTYUNDOBUFFER" },
  { 206, L"EM_GETFIRSTVISIBLELINE" },
  { 207, L"EM_SETREADONLY" },
  { 209, L"EM_SETWORDBREAKPROC" },
  { 209, L"EM_GETWORDBREAKPROC" },
  { 210, L"EM_GETPASSWORDCHAR" },
  { 211, L"EM_SETMARGINS" },
  { 212, L"EM_GETMARGINS" },
  { 213, L"EM_GETLIMITTEXT" },
  { 214, L"EM_POSFROMCHAR" },
  { 215, L"EM_CHARFROMPOS" },
  { 216, L"EM_SETIMESTATUS" },
  { 217, L"EM_GETIMESTATUS" },
  { 224, L"SBM_SETPOS" },
  { 225, L"SBM_GETPOS" },
  { 226, L"SBM_SETRANGE" },
  { 227, L"SBM_GETRANGE" },
  { 228, L"SBM_ENABLE_ARROWS" },
  { 230, L"SBM_SETRANGEREDRAW" },
  { 233, L"SBM_SETSCROLLINFO" },
  { 234, L"SBM_GETSCROLLINFO" },
  { 235, L"SBM_GETSCROLLBARINFO" },
  { 240, L"BM_GETCHECK" },
  { 241, L"BM_SETCHECK" },
  { 242, L"BM_GETSTATE" },
  { 243, L"BM_SETSTATE" },
  { 244, L"BM_SETSTYLE" },
  { 245, L"BM_CLICK" },
  { 246, L"BM_GETIMAGE" },
  { 247, L"BM_SETIMAGE" },
  { 248, L"BM_SETDONTCLICK" },
  { 255, L"WM_INPUT" },
  { 256, L"WM_KEYDOWN" },
  { 256, L"WM_KEYFIRST" },
  { 257, L"WM_KEYUP" },
  { 258, L"WM_CHAR" },
  { 259, L"WM_DEADCHAR" },
  { 260, L"WM_SYSKEYDOWN" },
  { 261, L"WM_SYSKEYUP" },
  { 262, L"WM_SYSCHAR" },
  { 263, L"WM_SYSDEADCHAR" },
  { 264, L"WM_KEYLAST" },
  { 265, L"WM_UNICHAR" },
  { 265, L"WM_WNT_CONVERTREQUESTEX" },
  { 266, L"WM_CONVERTREQUEST" },
  { 267, L"WM_CONVERTRESULT" },
  { 268, L"WM_INTERIM" },
  { 269, L"WM_IME_STARTCOMPOSITION" },
  { 270, L"WM_IME_ENDCOMPOSITION" },
  { 271, L"WM_IME_COMPOSITION" },
  { 271, L"WM_IME_KEYLAST" },
  { 272, L"WM_INITDIALOG" },
  { 273, L"WM_COMMAND" },
  { 274, L"WM_SYSCOMMAND" },
  { 275, L"WM_TIMER" },
  { 276, L"WM_HSCROLL" },
  { 277, L"WM_VSCROLL" },
  { 278, L"WM_INITMENU" },
  { 279, L"WM_INITMENUPOPUP" },
  { 280, L"WM_SYSTIMER" },
  { 287, L"WM_MENUSELECT" },
  { 288, L"WM_MENUCHAR" },
  { 289, L"WM_ENTERIDLE" },
  { 290, L"WM_MENURBUTTONUP" },
  { 291, L"WM_MENUDRAG" },
  { 292, L"WM_MENUGETOBJECT" },
  { 293, L"WM_UNINITMENUPOPUP" },
  { 294, L"WM_MENUCOMMAND" },
  { 295, L"WM_CHANGEUISTATE" },
  { 296, L"WM_UPDATEUISTATE" },
  { 297, L"WM_QUERYUISTATE" },
  { 306, L"WM_CTLCOLORMSGBOX" },
  { 307, L"WM_CTLCOLOREDIT" },
  { 308, L"WM_CTLCOLORLISTBOX" },
  { 309, L"WM_CTLCOLORBTN" },
  { 310, L"WM_CTLCOLORDLG" },
  { 311, L"WM_CTLCOLORSCROLLBAR" },
  { 312, L"WM_CTLCOLORSTATIC" },
  //{ 512, L"WM_MOUSEFIRST" },
  { 512, L"WM_MOUSEMOVE" },
  { 513, L"WM_LBUTTONDOWN" },
  { 514, L"WM_LBUTTONUP" },
  { 515, L"WM_LBUTTONDBLCLK" },
  { 516, L"WM_RBUTTONDOWN" },
  { 517, L"WM_RBUTTONUP" },
  { 518, L"WM_RBUTTONDBLCLK" },
  { 519, L"WM_MBUTTONDOWN" },
  { 520, L"WM_MBUTTONUP" },
  { 521, L"WM_MBUTTONDBLCLK" },
  //{ 521, L"WM_MOUSELAST" },
  { 522, L"WM_MOUSEWHEEL" },
  { 523, L"WM_XBUTTONDOWN" },
  { 524, L"WM_XBUTTONUP" },
  { 525, L"WM_XBUTTONDBLCLK" },
  { 528, L"WM_PARENTNOTIFY" },
  { 529, L"WM_ENTERMENULOOP" },
  { 530, L"WM_EXITMENULOOP" },
  { 531, L"WM_NEXTMENU" },
  { 532, L"WM_SIZING" },
  { 533, L"WM_CAPTURECHANGED" },
  { 534, L"WM_MOVING" },
  { 536, L"WM_POWERBROADCAST" },
  { 537, L"WM_DEVICECHANGE" },
  { 544, L"WM_MDICREATE" },
  { 545, L"WM_MDIDESTROY" },
  { 546, L"WM_MDIACTIVATE" },
  { 547, L"WM_MDIRESTORE" },
  { 548, L"WM_MDINEXT" },
  { 549, L"WM_MDIMAXIMIZE" },
  { 550, L"WM_MDITILE" },
  { 551, L"WM_MDICASCADE" },
  { 552, L"WM_MDIICONARRANGE" },
  { 553, L"WM_MDIGETACTIVE" },
  { 560, L"WM_MDISETMENU" },
  { 561, L"WM_ENTERSIZEMOVE" },
  { 562, L"WM_EXITSIZEMOVE" },
  { 563, L"WM_DROPFILES" },
  { 564, L"WM_MDIREFRESHMENU" },
  { 640, L"WM_IME_REPORT" },
  { 641, L"WM_IME_SETCONTEXT" },
  { 642, L"WM_IME_NOTIFY" },
  { 643, L"WM_IME_CONTROL" },
  { 644, L"WM_IME_COMPOSITIONFULL" },
  { 645, L"WM_IME_SELECT" },
  { 646, L"WM_IME_CHAR" },
  { 648, L"WM_IME_REQUEST" },
  { 656, L"WM_IMEKEYDOWN" },
  { 656, L"WM_IME_KEYDOWN" },
  { 657, L"WM_IMEKEYUP" },
  { 657, L"WM_IME_KEYUP" },
  { 672, L"WM_NCMOUSEHOVER" },
  { 673, L"WM_MOUSEHOVER" },
  { 674, L"WM_NCMOUSELEAVE" },
  { 675, L"WM_MOUSELEAVE" },
  { 768, L"WM_CUT" },
  { 769, L"WM_COPY" },
  { 770, L"WM_PASTE" },
  { 771, L"WM_CLEAR" },
  { 772, L"WM_UNDO" },
  { 773, L"WM_RENDERFORMAT" },
  { 774, L"WM_RENDERALLFORMATS" },
  { 775, L"WM_DESTROYCLIPBOARD" },
  { 776, L"WM_DRAWCLIPBOARD" },
  { 777, L"WM_PAINTCLIPBOARD" },
  { 778, L"WM_VSCROLLCLIPBOARD" },
  { 779, L"WM_SIZECLIPBOARD" },
  { 780, L"WM_ASKCBFORMATNAME" },
  { 781, L"WM_CHANGECBCHAIN" },
  { 782, L"WM_HSCROLLCLIPBOARD" },
  { 783, L"WM_QUERYNEWPALETTE" },
  { 784, L"WM_PALETTEISCHANGING" },
  { 785, L"WM_PALETTECHANGED" },
  { 786, L"WM_HOTKEY" },
  { 791, L"WM_PRINT" },
  { 792, L"WM_PRINTCLIENT" },
  { 793, L"WM_APPCOMMAND" },
  { 856, L"WM_HANDHELDFIRST" },
  { 863, L"WM_HANDHELDLAST" },
  { 864, L"WM_AFXFIRST" },
  { 895, L"WM_AFXLAST" },
  { 896, L"WM_PENWINFIRST" },
  { 897, L"WM_RCRESULT" },
  { 898, L"WM_HOOKRCRESULT" },
  { 899, L"WM_GLOBALRCCHANGE" },
  { 899, L"WM_PENMISCINFO" },
  { 900, L"WM_SKB" },
  { 901, L"WM_HEDITCTL" },
  { 901, L"WM_PENCTL" },
  { 902, L"WM_PENMISC" },
  { 903, L"WM_CTLINIT" },
  { 904, L"WM_PENEVENT" },
  { 911, L"WM_PENWINLAST" },
  { 1024, L"WM_USER" }
};

// https://docs.microsoft.com/en-us/windows/win32/winmsg/window-styles
XMSGITEM xwstyleslist[] =
{
	{ 0x00800000L, L"WS_BORDER"},
	{ 0x00C00000L, L"WS_CAPTION"},
	{ 0x40000000L, L"WS_CHILD"},
	{ 0x02000000L, L"WS_CLIPCHILDREN"},
	{ 0x04000000L, L"WS_CLIPSIBLINGS"},
	{ 0x08000000L, L"WS_DISABLED"},
	{ 0x00400000L, L"WS_DLGFRAME"},
	{ 0x00020000L, L"WS_GROUP"},
	{ 0x00100000L, L"WS_HSCROLL"},
	{ 0x01000000L, L"WS_MAXIMIZE"},
	{ 0x00010000L, L"WS_MAXIMIZEBOX"},
	{ 0x20000000L, L"WS_MINIMIZE"},
	{ 0x00020000L, L"WS_MINIMIZEBOX"},
	{ 0x80000000L, L"WS_POPUP"},
	{ 0x00040000L, L"WS_SIZEBOX"},
	{ 0x00080000L, L"WS_SYSMENU"},
	{ 0x00010000L, L"WS_TABSTOP"},
	{ 0x10000000L, L"WS_VISIBLE"},
	{ 0x00200000L, L"WS_VSCROLL"},
};

// https://docs.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
XMSGITEM xwstylesextendedlist[] =
{
	{ 0x00000001L, L"WS_EX_DLGMODALFRAME" },
	{ 0x00000004L, L"WS_EX_NOPARENTNOTIFY" },
	{ 0x00000008L, L"WS_EX_TOPMOST" },
	{ 0x00000010L, L"WS_EX_ACCEPTFILES" },
	{ 0x00000020L, L"WS_EX_TRANSPARENT" },
	{ 0x00000040L, L"WS_EX_MDICHILD" },
	{ 0x00000080L, L"WS_EX_TOOLWINDOW" },
	{ 0x00000100L, L"WS_EX_WINDOWEDGE" },
	{ 0x00000200L, L"WS_EX_CLIENTEDGE" },
	{ 0x00000400L, L"WS_EX_CONTEXTHELP" },
	{ 0x00001000L, L"WS_EX_RIGHT" },
	{ 0x00000000L, L"WS_EX_LEFT" },
	{ 0x00002000L, L"WS_EX_RTLREADING" },
	{ 0x00000000L, L"WS_EX_LTRREADING" },
	{ 0x00004000L, L"WS_EX_LEFTSCROLLBAR" },
	{ 0x00000000L, L"WS_EX_RIGHTSCROLLBAR" },
	{ 0x00010000L, L"WS_EX_CONTROLPARENT" },
	{ 0x00020000L, L"WS_EX_STATICEDGE" },
	{ 0x00040000L, L"WS_EX_APPWINDOW" },
	{ 0x00000300L, L"WS_EX_OVERLAPPEDWINDOW" },
	{ 0x00000188L, L"WS_EX_PALETTEWINDOW" },
	{ 0x00080000L, L"WS_EX_LAYERED" },
	{ 0x00100000L, L"WS_EX_NOINHERITLAYOUT" },
	{ 0x00200000L, L"WS_EX_NOREDIRECTIONBITMAP" },
	{ 0x00400000L, L"WS_EX_LAYOUTRTL" },
	{ 0x02000000L, L"WS_EX_COMPOSITED" },
	{ 0x08000000L, L"WS_EX_NOACTIVATE" },
};

XMSGITEM xhittestlist[] =
{
	{ HTTOPLEFT, L"HTTOPLEFT" },
	{ HTTOP, L"HTTOP" },
	{ HTTOPRIGHT, L"HTTOPRIGHT" },
	{ HTRIGHT, L"HTRIGHT" },
	{ HTBOTTOMRIGHT, L"HTBOTTOMRIGHT" },
	{ HTBOTTOM, L"HTBOTTOM" },
	{ HTBOTTOMLEFT, L"HTBOTTOMLEFT" },
	{ HTLEFT, L"HTLEFT" },
	{ HTSIZE, L"HTSIZE" },
	{ HTCLIENT, L"HTCLIENT" },
};

#define NUM_XMSGS (sizeof(xmsglist) / sizeof(XMSGITEM))
#define NUM_XWSTYLES (sizeof(xwstyleslist) / sizeof(XMSGITEM))
#define NUM_XWEXTENDEDSTYLES (sizeof(xwstylesextendedlist) / sizeof(XMSGITEM))
#define NUM_XHITTEST (sizeof(xhittestlist) / sizeof(XMSGITEM))



wstring GetHitTestText(DWORD nchittest)
{
	wstring outp;
	for (int i = 0; i < NUM_XHITTEST; i++)
	{
		auto& item = xhittestlist[i];
		if (item.code == nchittest)
		{
			return item.text;
		}
	}
	return std::to_wstring(nchittest);
}

wstring GetWindowStyleText(DWORD dwStyle)
{
	wstring outp;
	for (int i = 0; i < NUM_XWSTYLES; i++)
	{
		auto& item = xwstyleslist[i];
		if ((dwStyle & item.code) > 0)
		{
			if (outp.length() > 0)
			{
				outp = outp + L" | " + item.text;
			}
			else
			{
				outp = item.text;
			}
		}
	}
	return outp;
}

wstring GetWindowStyleExtendedText(DWORD dwStyle)
{
	wstring outp;
	for (int i = 0; i < NUM_XWEXTENDEDSTYLES; i++)
	{
		auto& item = xwstylesextendedlist[i];
		if ((dwStyle & item.code) > 0)
		{
			if (outp.length() > 0)
			{
				outp = outp + L" | " + item.text;
			}
			else
			{
				outp = item.text;
			}
		}
	}
	return outp;
}

wstring GetWndMsgTxt(UINT msg)
{
	for (int i = 0; i < NUM_XMSGS; i++)
	{
		auto& item = xmsglist[i];
		if (item.code == msg)
		{
			return item.text;
		}
	}
	return L"";
}

// 0x0020 - WM_SETCURSOR (45)
// 0x0084 - WM_NCHITTEST (26)
// 0x0113 - WM_TIMER (46)
// 0x0135 - WM_CTLCOLORBTN (8)
// 0x0200 - WM_MOUSEFIRST (26)

int wmmousestate = 0;
int wmmousecount = 0;

void PrintWndMsg(UINT msg, bool mouse)
{
	if (msg == WM_NCHITTEST || msg == WM_SETCURSOR || msg == WM_MOUSEMOVE) return;

	//if (msg == WM_NCHITTEST && wmmousestate == 0)
	//{
	//	wmmousestate = WM_NCHITTEST;
	//}
	//else if (msg == WM_SETCURSOR && wmmousestate == WM_NCHITTEST)
	//{
	//	wmmousestate = WM_SETCURSOR;
	//}
	//else if (msg == WM_MOUSEMOVE && wmmousestate == WM_SETCURSOR)
	//{
	//	if (mouse)
	//	{
	//		if (wmmousecount > 0)
	//		{
	//			// delete last line
	//			cout << "\r";
	//		}

	//		wcout << "mouse { WM_NCHITTEST, WM_SETCURSOR, WM_MOUSEMOVE } x" << wmmousecount;
	//		wmmousecount++;
	//	}
	//	wmmousestate = 0;
	//}
	//else
	//{
	//	if (wmmousecount > 0)
	//		cout << "\n";
	wmmousestate = wmmousecount = 0;
	for (int i = 0; i < NUM_XMSGS; i++)
	{
		auto& item = xmsglist[i];
		if (item.code == msg)
		{
			wcout << item.text << " (" << item.code << ")\n";
			break;
		}
	}
	//}
}

