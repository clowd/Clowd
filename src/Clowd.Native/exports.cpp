#include "pch.h"
#include "exports.h"

#include "BorderWindow.h"
#include "DxScreenCapture.h"

std::mutex sync;
BorderWindow* border = 0;
DxScreenCapture* capture = 0;

void __cdecl BorderShow(BYTE r, BYTE g, BYTE b, RECT* decoratedArea)
{
    std::lock_guard<std::mutex> guard(sync);

    if (border)
    {
        delete border;
        border = 0;
    }

    auto area = Rect2Gdi(decoratedArea);
    auto color = Gdiplus::Color(r, g, b);
    border = new BorderWindow(color, area);
}

void __cdecl BorderSetOverlayText(wchar_t* overlayTxt)
{
    std::lock_guard<std::mutex> guard(sync);
    if (border)
        border->SetOverlayText(std::wstring(overlayTxt ? overlayTxt : L""));
}

void __cdecl BorderClose()
{
    std::lock_guard<std::mutex> guard(sync);
    if (border)
    {
        delete border;
        border = 0;
    }
}

void __cdecl CaptureShow(captureArgs* args)
{
    std::lock_guard<std::mutex> guard(sync);

    if (capture)
    {
        delete capture;
        capture = 0;
    }

    capture = new DxScreenCapture(args);
}

void __cdecl CaptureClose()
{
    std::lock_guard<std::mutex> guard(sync);
    if (!capture) return;
    delete capture;
    capture = 0;
}