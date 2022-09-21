#pragma once
#include "pch.h"
#include "NativeBitmap.h"
#include "Screens.h"
#include "WindowWalker.h"
#include "DxOutputDevice.h"
#include "exports.h"

#define NUM_SVG_BUTTONS 7
typedef RECT buttonPositionsArr[NUM_SVG_BUTTONS + 1]; // last item (+1) is the sizing indicator

struct mc_frame_data
{
    int hittest;
    int hittestBtn;
    double zoom;
    bool animated;
    bool debugging;
    bool tips;
    bool crosshair;
    bool mouseDown;
    POINTFF mouse;
    POINTFF mouseDownPt;
    bool mouseAnchored;
    bool captured;
    bool dragging;

    RECT selection;
    HitV2Result windowSelection;
    buttonPositionsArr buttonPositions;
};

struct mc_window_native
{
    std::atomic<int> disposed;
    mc_frame_data frame;
    std::unique_ptr<NativeDib> screenshot;
    std::vector<DxDisplay> monitors;
    std::unique_ptr<Screens> screens;
    std::unique_ptr<WindowWalker> walker;

    std::chrono::steady_clock::time_point t0;
    std::chrono::steady_clock::time_point t1;
    std::chrono::steady_clock::time_point t2;
    std::chrono::steady_clock::time_point t3;
};

struct render_info
{
    unsigned int idx;
    void* pParent;
    HWND hWnd;
    unsigned int threadId;
    HANDLE threadHandle;
    HANDLE eventSignal;
};

class DxScreenCapture
{

private:
    int _vx;
    int _vy;
    captureArgs _options;
    std::mutex sync;
    HANDLE mainThread;
    unsigned int mainThreadId;
    std::vector<render_info> windows;
    std::vector<std::exception> errors;
    HWND primaryWindow;
    mc_window_native* native;
    CURSORINFO _cursorInfo;
    POINT _cursorPt;
    BOOL _cursorShowing;

    static unsigned int __stdcall MessagePumpProc(void* lpParam);
    void RunMessagePump();
    static unsigned int __stdcall RenderThreadProc(void* lpParam);
    void RunRenderLoop(const render_info& wi, const DxDisplay& mon, const ScreenInfo& myscreen);
    static LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
    LRESULT WndProcImpl(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

    void SetFrame(mc_frame_data* data);
    void FrameMakeSelection(mc_frame_data& data, const RECT& sel, bool preserveWindow);
    void FrameUpdateHitTest(mc_frame_data& data);
    void FrameSetCursor(mc_frame_data& data);
    void FrameUpdateHoveredWindow(mc_frame_data& data);
    System::Drawing::Color GetFrameHoveredColor(mc_frame_data* data);
    void DrawCursorToDC(HDC hdc, const RECT& sel);
    std::unique_ptr<NativeDib> GetCursorBitmap(RECT& pos);
    std::unique_ptr<NativeDib> GetCombinedBitmap(const mc_frame_data& data, bool flipV, bool cursor, const RECT& selection);
    std::unique_ptr<NativeDib> GetCombinedBitmap(bool flipV, bool cropped, bool cursor);
    //void WriteToPointer(void* data, int dataSize);

public:
    DxScreenCapture(captureArgs* options);
    ~DxScreenCapture();
    void Reset();
    void Close(bool waitForExit = false);
    System::String SaveSession(System::String sessionDirectory, System::String createdUtc);
    void WriteToClipboard();
    void GetFrame(mc_frame_data* data);
    void GetSelectionRect(RECT& rect)
    {
        rect = native->frame.selection;
        native->screens->TranslateToSystem(rect);
    }

};

