#include "pch.h"
#include "Screens.h"
#include "rectex.h"

using namespace std;

BOOL CALLBACK ScreensMonitorEnumProc(HMONITOR hMonitor, HDC hdcMonitor, LPRECT lprcMonitor, LPARAM dwData)
{
	std::vector<ScreenInfo>* infos = reinterpret_cast<vector<ScreenInfo>*>(dwData);

	MONITORINFOEX mon_info{};
	mon_info.cbSize = sizeof(MONITORINFOEX);
	GetMonitorInfo(hMonitor, &mon_info);

	wstring name(mon_info.szDevice);

	int index = infos->size();
	bool primary = (mon_info.dwFlags & MONITORINFOF_PRIMARY) > 0;

	UINT dpiX, dpiY;
	GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY);

	infos->emplace_back(hMonitor, index, dpiX, primary, mon_info.rcWork, mon_info.rcMonitor, mon_info.rcWork, mon_info.rcMonitor, name);
	return TRUE;
}

void Screens::GetDebugDetail(vector<ScreenDebugDisplay>& details, RECT& primaryDebugRect)
{
	primaryDebugRect.left = _primary->workspaceBounds.right - DEBUGBOX_MARGIN - DEBUGBOX_SIZE;
	primaryDebugRect.top = _primary->workspaceBounds.top + DEBUGBOX_MARGIN;
	primaryDebugRect.right = _primary->workspaceBounds.right - DEBUGBOX_MARGIN;
	primaryDebugRect.bottom = _primary->workspaceBounds.top + DEBUGBOX_MARGIN + DEBUGBOX_SIZE;

	for (int i = 0; i < _monitors.size(); i++)
	{
		ScreenInfo& mon = _monitors[i];

		RECT renderRect = {
			mon.workspaceBounds.left + DEBUGBOX_MARGIN,
			mon.workspaceBounds.top + DEBUGBOX_MARGIN,
			mon.workspaceBounds.left + DEBUGBOX_MARGIN + DEBUGBOX_SIZE,
			mon.workspaceBounds.top + DEBUGBOX_MARGIN + DEBUGBOX_SIZE
		};

		wstring detail = to_wstring(mon.index) + L": " + mon.name;
		if (mon.primary) detail += L" (PRIMARY)";
		detail += L" DPI-" + to_wstring(mon.dpi) + L"\n";

		detail += L"real_position: " + Rect2String(&mon.realBounds) + L"\n";
		detail += L"workspace_position: " + Rect2String(&mon.workspaceBounds) + L"\n";

		details.emplace_back(renderRect, detail);
	}
}

bool Screens::IsAnchorPt(POINT& pt)
{
	return _anchor.x == pt.x && _anchor.y == pt.y;
}

int Screens::VirtX()
{
	return _virtX;
}
int Screens::VirtY()
{
	return _virtY;
}

void Screens::TranslateToWorkspace(RECT& rect)
{
	rect.left -= _virtX;
	rect.top -= _virtY;
	rect.right -= _virtX;
	rect.bottom -= _virtY;
}

void Screens::TranslateToSystem(RECT& rect)
{
	rect.left += _virtX;
	rect.top += _virtY;
	rect.right += _virtX;
	rect.bottom += _virtY;
}

Screens::Screens()
{
	_monitors.clear();
	_monitors.reserve(6); // who has more than 6 monitors.. seriously?

	HDC screenHDC = GetDC(HWND_DESKTOP);
	EnumDisplayMonitors(screenHDC, 0, ScreensMonitorEnumProc, reinterpret_cast<LPARAM>(&_monitors));
	ReleaseDC(HWND_DESKTOP, screenHDC);

	_primary = &_monitors[0];

	_virtX = GetSystemMetrics(SM_XVIRTUALSCREEN);
	_virtY = GetSystemMetrics(SM_YVIRTUALSCREEN);
	int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
	int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
	_workspace = { 0, 0, width, height };

	for (int i = 0; i < _monitors.size(); i++)
	{
		ScreenInfo& mon = _monitors[i];
		if (mon.primary) _primary = &_monitors[i];
		mon.workspaceBounds = { mon.realBounds.left - _virtX, mon.realBounds.top - _virtY, mon.realBounds.right - _virtX, mon.realBounds.bottom - _virtY, };
		mon.workspaceWork = { mon.realWork.left - _virtX, mon.realWork.top - _virtY, mon.realWork.right - _virtX, mon.realWork.bottom - _virtY, };
	}

	_anchor = {
		(_primary->realBounds.right - _primary->realBounds.left) / 2,
		(_primary->realBounds.bottom - _primary->realBounds.top) / 2
	};

}

const ScreenInfo& Screens::ScreenFromHMONITOR(HMONITOR hMon)
{
	for (int i = 0; i < _monitors.size(); i++)
	{
		if (_monitors[i].hMonitor == hMon)
		{
			return _monitors[i];
		}
	}
	return *_primary;
}

void Screens::MouseAnchorStart(POINTFF& position)
{
	POINT pt{};
	GetCursorPos(&pt);
	position.x = pt.x - _virtX;
	position.y = pt.y - _virtY;
	SetCursorPos(_anchor.x, _anchor.y);
}

void Screens::MouseAnchorUpdate(POINTFF& position, POINT& sysPt, double zoom)
{
	double xDelta = (sysPt.x - _anchor.x) / zoom;
	double yDelta = (sysPt.y - _anchor.y) / zoom;

	double mX = position.x + xDelta;
	double mY = position.y + yDelta;

	// clip cursor to nearest monitor
	POINTFF mm = { mX, mY };
	auto screen = ScreenFromWorkspacePt(mm);
	RECT& b = screen.workspaceBounds;

	mX = min(max(mX, b.left), b.right - 0.001);
	mY = min(max(mY, b.top), b.bottom - 0.001);

	position.x = mX;
	position.y = mY;

	SetCursorPos(_anchor.x, _anchor.y);
}

void Screens::MouseAnchorStop(POINTFF& position)
{
	int x = ((int)floor(position.x)) + _virtX;
	int y = ((int)floor(position.y)) + _virtY;

	SetCursorPos(x, y);
}

POINTFF Screens::ToWorkspacePt(const POINT& systemPt)
{
	return { (double)systemPt.x - _virtX, (double)systemPt.y - _virtY };
}

POINT Screens::ToSystemPt(const POINTFF& workspacePt)
{
	return { (int)floor(workspacePt.x) + _virtX, (int)floor(workspacePt.y) + _virtY };
}

const RECT& Screens::WorkspaceBounds()
{
	return _workspace;
}

const ScreenInfo& Screens::ScreenFromWorkspaceRect(RECT& rect)
{
    RECT scrRect = rect;
    TranslateToSystem(scrRect);
    return ScreenFromSystemRect(scrRect);
}

const ScreenInfo& Screens::ScreenFromSystemRect(RECT& rect)
{
    HMONITOR hMon = MonitorFromRect(&rect, MONITOR_DEFAULTTONEAREST);
    return ScreenFromHMONITOR(hMon);
}

const ScreenInfo& Screens::ScreenFromWorkspacePt(POINTFF& pt)
{
	POINT scrPt = { (int)floor(pt.x) + _virtX, (int)floor(pt.y) + _virtY };
    return ScreenFromSystemPt(scrPt);
}

const ScreenInfo& Screens::ScreenFromSystemPt(POINT& pt)
{
	HMONITOR hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
	return ScreenFromHMONITOR(hMon);
}
