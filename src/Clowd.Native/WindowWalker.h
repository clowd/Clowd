#pragma once
#include "pch.h"
#include "Screens.h"
#include "rectex.h"
#include <shobjidl_core.h>
#include "NativeBitmap.h"

using namespace std;

const CLSID CLSID_ImmersiveShell = { 0xC2F03A33, 0x21F5, 0x47FA, 0xB4, 0xBB, 0x15, 0x63, 0x62, 0xA2, 0xF2, 0x39 };

class WindowInfo
{

private:
	unique_ptr<NativeDib> _dib;
	std::chrono::steady_clock::time_point _wndPaintStart;
	std::chrono::steady_clock::time_point _wndPaintEnd;

public:
	HWND hWnd;
	RECT rcWorkspace;
	RECT rcTrueWindow;
	RECT rcWindow;
	std::wstring caption;
	DWORD style;
	DWORD exStyle;
	std::wstring className;
	bool captured;
	int index;
	bool obstructed;
	vector<RECT> obstructionRects;

	WindowInfo(
		HDC screenDC, HWND hWnd, std::wstring caption, std::wstring className,
		DWORD style, DWORD exStyle, RECT rcWorkspace, RECT rcTrueWindow, RECT rcWindow, int index) :
		hWnd(hWnd), obstructed(false), index(index),
		caption(caption), className(className), style(style), exStyle(exStyle),
		captured(false), rcWorkspace(rcWorkspace), rcTrueWindow(rcTrueWindow), rcWindow(rcWindow) { }

	double GetTimeToRender();
	void PrintImage();
	bool GetBitmap(NativeDib** dib, int* cropX, int* cropY);
	void BitBltImage(HDC dest, int x, int y);

};

struct HitV2Rect
{
	HWND hWnd;
	LONG style;
	LONG exStyle;
	bool scrollH;
	bool scrollV;
	bool visible;
	RECT workspaceRect;
	wchar_t reason[32];

	HitV2Rect() : hWnd(0), style(0), exStyle(0), scrollH(false), scrollV(false), visible(false), workspaceRect({}) {}

	//HitV2Rect(HWND hWnd, bool scrollH, bool scrollV, RECT& workspaceRect)
	//	: hWnd(hWnd), scrollH(scrollH), scrollV(scrollV), workspaceRect(workspaceRect) {}
};

struct HitV2Result
{
	WindowInfo* window;
	HitV2Rect rects[10];
	int cRects;
	RECT idealSelection;
};

class WindowWalker
{

private:
	HDC _screenDC;
	Screens* _screens;
	unique_ptr<Gdiplus::Region> _excludedArea;
	vector<WindowInfo> _topWindows;
	IVirtualDesktopManager* _vd;
	void EnumWindowCallback(HWND hWnd);
	static BOOL CALLBACK EnumWindowProc(HWND hWnd, LPARAM lParam);
	void GetWndTrueBounds(HWND hWnd, WINDOWINFO& winfo, RECT* bounds);
	WindowInfo* PointToWindow(const POINT& p);

public:
	WindowWalker(Screens* screens);
	~WindowWalker();
	void ShapshotTopLevel();
	bool HitTestV2(const POINTFF& wp, HitV2Result* hitResult);
	void ResetHitResult(HitV2Result* hitResult);
	vector<WindowInfo>* List();

	//vector<RECT> _rectangles;
	//void Process24bbpBitmapRectangles(int width, int height, void* scan0);

};

