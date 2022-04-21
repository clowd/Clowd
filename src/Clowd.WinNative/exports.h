#pragma once

enum CaptureType
{
    Upload = 1,
    Photo = 2,
    Save = 3,
};

typedef void(__cdecl* fnColorCapture)(const BYTE r, const BYTE g, const BYTE b);
typedef void(__cdecl* fnVideoCapture)(const RECT captureRegion);
typedef void(__cdecl* fnSessionCapture)(const wchar_t* sessionJsonPath, CaptureType captureType);
typedef void(__cdecl* fnDisposed)(const wchar_t* errorMsg);

typedef struct captureArgs
{
    BYTE colorR;
    BYTE colorG;
    BYTE colorB;
    BOOL animationDisabled;
    BOOL obstructedWindowDisabled;
    BOOL tipsDisabled;
    fnColorCapture lpfnColorCapture;
    fnVideoCapture lpfnVideoCapture;
    fnSessionCapture lpfnSessionCapture;
    fnDisposed lpfnDisposed;
    wchar_t sessionDirectory[512];
    wchar_t createdUtc[128];
};

extern "C"
{
    // BorderWindow
    __declspec(dllexport) void __cdecl BorderShow(BYTE r, BYTE g, BYTE b, RECT* decoratedArea);
    __declspec(dllexport) void __cdecl BorderSetOverlayText(wchar_t* overlayTxt);
    __declspec(dllexport) void __cdecl BorderClose();

    // DxScreenCapture
    __declspec(dllexport) void __cdecl CaptureShow(captureArgs* args);
    __declspec(dllexport) void __cdecl CaptureClose();
    //__declspec(dllexport) void __cdecl CaptureReset();
    //__declspec(dllexport) RECT __cdecl CaptureGetSelectedArea();
    //__declspec(dllexport) void __cdecl CaptureWriteSessionToFile(wchar_t* sessionDirectory, wchar_t* createdUtc);
    //__declspec(dllexport) void __cdecl CaptureWriteSessionToClipboard();
}