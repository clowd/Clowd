#include "pch.h"
#include "CursorInfoEx.h"

using namespace std;

#define CURSORS_SUBKEY L"Control Panel\\Cursors"
#define ACCESSIBILITY_SUBKEY L"SOFTWARE\\Microsoft\\Accessibility"
#define ACCESSIBILITY_NAME L"CursorSize"
#define CURSORBASESIZE L"CursorBaseSize"

CursorInfoEx::CursorInfoEx()
{
    _cursors[LoadCursor(0, IDC_ARROW)] = { 32512, L"Arrow" };
    _cursors[LoadCursor(0, IDC_IBEAM)] = { 32513, L"IBeam" };
    _cursors[LoadCursor(0, IDC_WAIT)] = { 32514, L"Wait" };
    _cursors[LoadCursor(0, IDC_CROSS)] = { 32515, L"Crosshair" };
    _cursors[LoadCursor(0, IDC_UPARROW)] = { 32516, L"UpArrow" };
    _cursors[LoadCursor(0, IDC_SIZE)] = { 32640, L"SizeAll" };
    _cursors[LoadCursor(0, IDC_ICON)] = { 32641, L"Arrow" };
    _cursors[LoadCursor(0, IDC_SIZENWSE)] = { 32642, L"SizeNWSE" };
    _cursors[LoadCursor(0, IDC_SIZENESW)] = { 32643, L"SizeNESW" };
    _cursors[LoadCursor(0, IDC_SIZEWE)] = { 32644, L"SizeWE" };
    _cursors[LoadCursor(0, IDC_SIZENS)] = { 32645, L"SizeNS" };
    _cursors[LoadCursor(0, IDC_SIZEALL)] = { 32646, L"SizeAll" };
    _cursors[LoadCursor(0, IDC_NO)] = { 32648, L"No" };
    _cursors[LoadCursor(0, IDC_HAND)] = { 32649, L"Hand" };
    _cursors[LoadCursor(0, IDC_APPSTARTING)] = { 32650, L"AppStarting" };
    _cursors[LoadCursor(0, IDC_HELP)] = { 32651, L"Help" };
    _cursors[LoadCursor(0, IDC_PIN)] = { 32671, L"Pin" };
    _cursors[LoadCursor(0, IDC_PERSON)] = { 32672, L"Person" };
}

DWORD GetCursorAccessibilityMultiplier()
{
    DWORD data, type, size = sizeof(DWORD);
    if (ERROR_SUCCESS == RegGetValue(HKEY_CURRENT_USER, ACCESSIBILITY_SUBKEY, ACCESSIBILITY_NAME, RRF_RT_REG_DWORD, &type, &data, &size)) {
        return data;
    }
    return 1;
}

SIZE GetCursorSizeForMonitor(const POINT& pt)
{
    // get monitor dpi
    HMONITOR hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    UINT dpiX = 96, dpiY = 96;
    if (hMon && GetDpiForMonitor(hMon, MDT_DEFAULT, &dpiX, &dpiY) != S_OK) {
        dpiX = dpiY = 96;
    }

    auto cx = GetSystemMetricsForDpi(SM_CXCURSOR, dpiX) / (double)GetSystemMetricsForDpi(SM_CXCURSOR, 96);
    auto cy = GetSystemMetricsForDpi(SM_CYCURSOR, dpiY) / (double)GetSystemMetricsForDpi(SM_CYCURSOR, 96);

    // try to get the cursor size from the registry
    if (GetCursorAccessibilityMultiplier() != 1) {
        DWORD data, type, size = sizeof(DWORD);
        if (ERROR_SUCCESS == RegGetValue(HKEY_CURRENT_USER, CURSORS_SUBKEY, CURSORBASESIZE, RRF_RT_REG_DWORD, &type, &data, &size)) {
            return { (LONG)round(data*cx), (LONG)round(data*cy) };
        }
    }

    // if that fails get the default size
    return { GetSystemMetricsForDpi(SM_CXCURSOR, dpiX), GetSystemMetricsForDpi(SM_CYCURSOR, dpiY) };
}

wstring GetCursorFilePath(const wstring& keyName)
{
    DWORD type = 0, size = 0;
    if (ERROR_SUCCESS == RegGetValue(HKEY_CURRENT_USER, CURSORS_SUBKEY, keyName.c_str(), RRF_RT_REG_SZ, &type, nullptr, &size) && size > 10) {
        wstring str{};
        str.resize(size);
        if (ERROR_SUCCESS == RegGetValue(HKEY_CURRENT_USER, CURSORS_SUBKEY, keyName.c_str(), RRF_RT_REG_SZ, &type, str.data(), &size)) {
            return str;
            /*  size = ExpandEnvironmentStrings(str.c_str(), nullptr, 0);
            wstring expanded{};
            expanded.resize(size);
            if (ExpandEnvironmentStrings(str.c_str(), expanded.data(), size) == size) {
                return expanded;
            }*/
        }
    }

    wstring empty;
    return empty;
}

unique_ptr<CursorData> CursorInfoEx::SnapshotCurrent()
{
    ICONINFO ii;
    CURSORINFO ci;
    ci.cbSize = sizeof(ci);

    if (!GetCursorInfo(&ci) || ci.flags != CURSOR_SHOWING) {
        return nullptr;
    }

    // Check if it is a system cursor and try to scale it nicely. This will handle per-monitor-dpi scaling
    // and also the "accessibility" scaling in the new Win10 settings.
    auto search = _cursors.find(ci.hCursor);
    if (search != _cursors.end())
    {
        SIZE sz = GetCursorSizeForMonitor(ci.ptScreenPos);

        // get the path to the .cur file so we can load a custom file
        wstring cursorPath = GetCursorFilePath(search->second.regKeyName);
        if (!cursorPath.empty()) {
            HICON hico = (HICON)LoadImage(0, cursorPath.c_str(), IMAGE_CURSOR, sz.cx, sz.cy, LR_LOADFROMFILE);
            if (hico)
            {
                if (GetIconInfo(hico, &ii))
                {
                    // this function can fail, but if it suceeds it can produce better bitmaps because in the absence of an exact match
                    // it will grab the next biggest size and scale down, whereas LoadImage will grab the smaller size and upscale
                    // https://marc.durdin.net/2016/05/loadiconwithscaledown-and-loadiconmetric-fail-when-loading-ico-for-the-sm_cxiconsm_cyicon-size/
                    HICON hScaled;
                    if (S_OK == LoadIconWithScaleDown(0, cursorPath.c_str(), sz.cx, sz.cy, &hScaled))
                    {
                        DestroyIcon(hico);
                        hico = hScaled;
                    }

                    auto x = ci.ptScreenPos.x - ii.xHotspot;
                    auto y = ci.ptScreenPos.y - ii.yHotspot;
                    RECT lc{ x, y, x + sz.cx, y + sz.cy };
                    return std::make_unique<CursorData>(hico, ii, lc);
                }
                else
                {
                    DestroyIcon(hico);
                }
            }
        }
    }

    // we were unable to find the "best" cursor, so we're going to just draw exactly what we have
    auto copy = CopyIcon(ci.hCursor);
    if (GetIconInfo(copy, &ii))
    {
        BITMAP bm;
        if (GetObject(ii.hbmMask, sizeof(bm), &bm) == sizeof(bm)) {
            SIZE psiz;
            psiz.cx = bm.bmWidth;
            psiz.cy = ii.hbmColor ? bm.bmHeight : bm.bmHeight / 2;
            auto x = ci.ptScreenPos.x - ii.xHotspot;
            auto y = ci.ptScreenPos.y - ii.yHotspot;
            RECT lc{ x, y, x + psiz.cx, y + psiz.cy };
            return std::make_unique<CursorData>(copy, ii, lc);
        }
    }
    else
    {
        DestroyIcon(copy);
    }

    return nullptr;
}

CursorData::CursorData(HCURSOR hCursor, ICONINFO iconInfo, RECT r)
{
    _hCursor = hCursor;
    _ii = iconInfo;
    Location = r;
}

CursorData::~CursorData()
{
    DeleteObject(_ii.hbmColor);
    DeleteObject(_ii.hbmMask);
    DestroyIcon(_hCursor);
}

void CursorData::DrawCursor(HDC hdc, int x, int y)
{
    DrawIconEx(hdc, x, y, _hCursor, 0, 0, 0, 0, DI_NORMAL);
}

