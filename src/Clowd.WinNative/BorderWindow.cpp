#include "pch.h"
#include "BorderWindow.h"
#include "rectex.h"
#include "outline-text/OutlineText.h"

std::string statusString(const Gdiplus::Status status) {
    switch (status) {
    case Gdiplus::Ok: return "Ok";
    case Gdiplus::GenericError: return "GenericError";
    case Gdiplus::InvalidParameter: return "InvalidParameter";
    case Gdiplus::OutOfMemory: return "OutOfMemory";
    case Gdiplus::ObjectBusy: return "ObjectBusy";
    case Gdiplus::InsufficientBuffer: return "InsufficientBuffer";
    case Gdiplus::NotImplemented: return "NotImplemented";
    case Gdiplus::Win32Error: return "Win32Error";
    case Gdiplus::Aborted: return "Aborted";
    case Gdiplus::FileNotFound: return "FileNotFound";
    case Gdiplus::ValueOverflow: return "ValueOverflow";
    case Gdiplus::AccessDenied: return "AccessDenied";
    case Gdiplus::UnknownImageFormat: return "UnknownImageFormat";
    case Gdiplus::FontFamilyNotFound: return "FontFamilyNotFound";
    case Gdiplus::FontStyleNotFound: return "FontStyleNotFound";
    case Gdiplus::NotTrueTypeFont: return "NotTrueTypeFont";
    case Gdiplus::UnsupportedGdiplusVersion: return "UnsupportedGdiplusVersion";
    case Gdiplus::GdiplusNotInitialized: return "GdiplusNotInitialized";
    case Gdiplus::PropertyNotFound: return "PropertyNotFound";
    case Gdiplus::PropertyNotSupported: return "PropertyNotSupported";
    default: return "Status Type Not Found.";
    }
}

inline void RGDI(const Gdiplus::Status status)
{
    if (status == Gdiplus::Status::Ok)
        return;

    std::string msg("GDI+ status error: " + statusString(status));
    throw std::exception(msg.c_str());
}

using namespace Gdiplus;

BorderWindow::BorderWindow(System::Drawing::Color color, System::Drawing::Rectangle area)
{
    _threadId = std::hash<std::thread::id>{}(std::this_thread::get_id());
    _disposed = false;
    _hInstance = GetModuleHandle(0);
    std::wstring clsName = L"ClowdBorderWindow-" + std::to_wstring(clock());
    const wchar_t* umClsName = clsName.c_str();

    WNDCLASS wc{};
    wc.lpfnWndProc = BorderWindow::WndProc;
    wc.hInstance = _hInstance;
    wc.lpszClassName = umClsName;

    SetLastError(S_OK);

    ATOM atom = RegisterClass(&wc);
    if (atom == 0)
        HR_last_error();

    _window = CreateWindowEx(
        WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
        umClsName,
        L"ClowdBorderWindow",
        WS_POPUP | WS_VISIBLE,
        0, 0, 1, 1,
        nullptr,
        nullptr,
        _hInstance,
        nullptr
    );

    if (_window == 0)
        HR_last_error();

    SetWindowLongPtr(_window, GWL_USERDATA, reinterpret_cast<LONG_PTR>(this));

    int dwFlag = 1;
    DwmSetWindowAttribute(_window, DWMWA_TRANSITIONS_FORCEDISABLED, &dwFlag, sizeof(int));
    DwmSetWindowAttribute(_window, DWMWA_EXCLUDED_FROM_PEEK, &dwFlag, sizeof(int));

    RECT r;
    Gdi2Rect(area, &r);

    UINT dpiX, dpiY;
    HMONITOR hMon = MonitorFromRect(&r, MONITOR_DEFAULTTONEAREST);
    HR(GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, &dpiX, &dpiY));

    double zoom = dpiX / BASE_DPI;
    _lineWidth = (int)floor(2 * zoom);
    _lineColor = color;

    area.Inflate(_lineWidth + 1, _lineWidth + 1);
    _position = area;

    UpdateLayer();
}

BorderWindow::~BorderWindow()
{
    if (_disposed) return;
    _disposed = true;

    auto ctid = std::hash<std::thread::id>{}(std::this_thread::get_id());
    if (ctid == _threadId)
    {
        // current thread owns this window
        DestroyWindow(_window);
    }
    else
    {
        // cur thread does not own window, so we need to post a message and ask 
        PostMessage(_window, WM_CLOSE, 0, 0);
    }
}

void BorderWindow::SetOverlayText(std::wstring& txt)
{
    _overlayTxt = txt;
    UpdateLayer();
    InvalidateRect(_window, NULL, FALSE);
}


void BorderWindow::UpdateLayer()
{
    if (_position.IsEmptyArea())
        return;

    // init resources
    Rect bounds(0, 0, _position.Width, _position.Height);
    HDC screenDC = GetDC(HWND_DESKTOP);
    HDC memoryDC = CreateCompatibleDC(screenDC);
    HBITMAP hBitmap = CreateCompatibleBitmap(screenDC, bounds.Width, bounds.Height);
    HGDIOBJ hOld = SelectObject(memoryDC, hBitmap);

    Color accentColor(_lineColor.GetR(), _lineColor.GetG(), _lineColor.GetB());
    Color complimentaryColor(255, 255, 255, 255);

    // draw layer
    Graphics g(memoryDC);
    g.Clear(Color::Black);
    g.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
    g.SetSmoothingMode(Gdiplus::SmoothingModeHighQuality);
    g.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);

    RectF outline(_lineWidth / 2.0, _lineWidth / 2.0, bounds.Width - _lineWidth, bounds.Height - _lineWidth);
    Pen borderPen(accentColor, _lineWidth);
    RGDI(g.DrawRectangle(&borderPen, outline));

    FLOAT offset = _lineWidth / 2.0 + 0.5;
    outline.X += offset;
    outline.Y += offset;
    offset *= 2;
    outline.Width -= offset;
    outline.Height -= offset;
    Pen whitePen(Color::White, 1);
    RGDI(g.DrawRectangle(&whitePen, outline));

    // draw text
    if (!_overlayTxt.empty())
    {
        Color gclr(255, _lineColor.GetR(), _lineColor.GetG(), _lineColor.GetB());
        SolidBrush gbrush(gclr);
        FontFamily gfont(FONT_ARIAL_BLACK);

        StringFormat format;
        format.SetAlignment(Gdiplus::StringAlignmentCenter);
        format.SetLineAlignment(Gdiplus::StringAlignmentCenter);

        TextDesigner::OutlineText text;
        text.TextOutline(accentColor, complimentaryColor, 8);
        text.DrawString(&g, &gfont, Gdiplus::FontStyleRegular, 48, _overlayTxt.c_str(), bounds, &format);
    }

    // update layer
    BLENDFUNCTION bf{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
    POINT origin{ 0, 0 };
    POINT pt{ _position.X, _position.Y };
    SIZE sz{ _position.Width, _position.Height };

    SetLastError(0);
    if (!UpdateLayeredWindow(_window, screenDC, &pt, &sz, memoryDC, &origin, 0, &bf, ULW_ALPHA))
        HR_last_error();

    // clean up
    SelectObject(memoryDC, hOld);
    DeleteObject(hBitmap);
    DeleteDC(memoryDC);
    ReleaseDC(HWND_DESKTOP, screenDC);
}

LRESULT BorderWindow::WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    BorderWindow* pInstance = reinterpret_cast<BorderWindow*>(GetWindowLongPtr(hWnd, GWL_USERDATA));
    if (pInstance == NULL) {
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    switch (msg)
    {

    case WM_PAINT:
    {
        ValidateRect(pInstance->_window, NULL);
        pInstance->UpdateLayer();
        return 0;
    }

    case WM_ERASEBKGND:
    {
        return TRUE;
    }

    case WM_NCHITTEST:
    {
        return HTTRANSPARENT;
    }

    case WM_CLOSE:
    {
        pInstance->_disposed = true;
        DestroyWindow(pInstance->_window);
        return 0;
    }

    }

    return DefWindowProc(hWnd, msg, wParam, lParam);
}
