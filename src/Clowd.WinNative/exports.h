#pragma once

typedef void(__cdecl* fnKeyPressed)(const DWORD keyCode);
typedef void(__cdecl* fnColorCaptured)(const BYTE r, const BYTE g, const BYTE b);
typedef void(__cdecl* fnLayoutUpdated)(const BOOL captured, const RECT area);
typedef void(__cdecl* fnDisposed)(const wchar_t* errorMsg);

typedef struct captureArgs
{
    BYTE colorR;
    BYTE colorG;
    BYTE colorB;
    BOOL animationDisabled;
    BOOL obstructedWindowDisabled;
    BOOL tipsDisabled;
    fnKeyPressed lpfnKeyPressed;
    fnColorCaptured lpfnColorCaptured;
    fnLayoutUpdated lpfnLayoutUpdated;
    fnDisposed lpfnDisposed;
};

extern "C"
{
    // BorderWindow
    __declspec(dllexport) void __cdecl BorderShow(BYTE r, BYTE g, BYTE b, RECT* decoratedArea);
    __declspec(dllexport) void __cdecl BorderSetOverlayText(wchar_t* overlayTxt);
    __declspec(dllexport) void __cdecl BorderClose();

    // DxScreenCapture
    __declspec(dllexport) void __cdecl CaptureShow(captureArgs args);
    __declspec(dllexport) void __cdecl CaptureReset();
    __declspec(dllexport) RECT __cdecl CaptureGetSelectedArea();
    __declspec(dllexport) void __cdecl CaptureClose();
    __declspec(dllexport) void __cdecl CaptureWriteSessionToFile(wchar_t* sessionDirectory, wchar_t* createdUtc);
    __declspec(dllexport) void __cdecl CaptureWriteSessionToClipboard();
}