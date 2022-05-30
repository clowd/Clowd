#include "pch.h"
#include "DxScreenCapture.h"
#include "rectex.h"
#include "Resource.h"
#include "winmsg.h"
#include "json.hpp"
#include <filesystem>

namespace fs = std::filesystem;
using json = nlohmann::json;

#define WAIT_FOR_INIT (6000)
#define EDGE_PADDING (50)
//#define DASH_LENGTH (8) // this gets scaled by line width so we leave it unscaled here

#define PIXEL_SELECTION_ROUNDING_THRESHOLD ((double)0.2)

#define UNSCALED_AREA_FONTSIZE_PX ((int)14)
#define UNSCALED_AREA_PADDING ((int)10)
#define UNSCALED_AREA_LINEWIDTH ((int)3)
#define UNSCALED_DRAG_THRESHOLD ((int)6)
#define UNSCALED_CURSOR_PART_LENGTH ((int)50)
#define UNSCALED_DRAG_HANDLE_SIZE ((int)10)
#define UNSCALED_BUTTON_SIZE ((int)50)
#define UNSCALED_BUTTON_ICON_SIZE ((int) 26)

using namespace std;

struct PPAIR
{
    int low;
    int high;
};

typedef struct
{
    LONG hit;
    RECT pt;
} RECTHT;

typedef struct
{
    DWORD resourceId;
    const wchar_t* text;
    unsigned int underlineIndex;
    DWORD vKeyCode;
    BOOL primary;
    ID2D1SvgDocument* svg;

} SVG_BUTTON_DESCRIPTION;

SVG_BUTTON_DESCRIPTION captureButtonDetails[NUM_SVG_BUTTONS] = {
    { IDR_SVG7, L"UPLOAD", 0, 0x55, true, nullptr },
    { IDR_SVG3, L"EDIT", 0, 0x50, true, nullptr },
    { IDR_SVG6, L"VIDEO", 0, 0x56, true, nullptr },
    { IDR_SVG1, L"COPY", 0, 0x43, true, nullptr },
    { IDR_SVG5, L"SAVE", 0, 0x53, true, nullptr },
    { IDR_SVG4, L"RESET", 0, 0x52, false, nullptr },
    { IDR_SVG2, L"EXIT", 1, 0x58, false, nullptr },
};

inline int _RoundPixel(double px, bool preferDown)
{
    int pfloor = (int)floor(px);
    double position = px - pfloor;
    double cutRatio = preferDown ? (1 - PIXEL_SELECTION_ROUNDING_THRESHOLD) : PIXEL_SELECTION_ROUNDING_THRESHOLD;
    return 1.0 * cutRatio > position ? pfloor : (int)ceil(px);
}

inline PPAIR _RoundPixelPair(double v1, double v2)
{
    double vmin = min(v1, v2);
    double vmax = max(v1, v2);
    return { _RoundPixel(vmin, true), _RoundPixel(vmax, false) };
}

RECT round_px_selection(double x1, double y1, double x2, double y2)
{
    PPAIR horz = _RoundPixelPair(x1, x2);
    PPAIR vert = _RoundPixelPair(y1, y2);
    return { horz.low, vert.low, horz.high, vert.high };
}

inline void ClipRectBy(D2D1_RECT_F* clip, const RECT* by)
{
    clip->left = max(clip->left, by->left);
    clip->top = max(clip->top, by->top);
    clip->right = min(clip->right, by->right);
    clip->bottom = min(clip->bottom, by->bottom);
}

D2D1_RECT_F TranslateFromWorkspaceToScreen(int vx, int vy, const RECT& bounds, const D2D1_RECT_F& rect)
{
    D2D1_RECT_F ret{};
    ret.left = rect.left - bounds.left + vx;
    ret.top = rect.top - bounds.top + vy;
    ret.right = rect.right - bounds.left + vx;
    ret.bottom = rect.bottom - bounds.top + vy;
    return ret;
}

D2D1_RECT_F TranslateFromWorkspaceToScreen(int vx, int vy, const RECT& bounds, const RECT& rect)
{
    D2D1_RECT_F ret{};
    ret.left = rect.left;
    ret.top = rect.top;
    ret.right = rect.right;
    ret.bottom = rect.bottom;
    return TranslateFromWorkspaceToScreen(vx, vy, bounds, ret);
}

void SetButtonPanelPositions(const ScreenInfo& screen, const RECT* selectionRect, buttonPositionsArr& buttonPos)
{
    double dpiZoom = screen.dpi / BASE_DPI;

    BOOL vert;
    auto screenBounds = Rect2Gdiplus(&screen.workspaceBounds);

    // padding / button measurements
    int minDistance = (int)ceil(2 * dpiZoom);
    int maxDistance = (int)ceil(15 * dpiZoom);
    int buttonSpacing = (int)ceil(3 * dpiZoom);
    int svgButtonSize = (int)floor(UNSCALED_BUTTON_SIZE * dpiZoom);
    int areaSize = (int)floor(svgButtonSize);// * 0.75);
    int longEdgePx = svgButtonSize * NUM_SVG_BUTTONS + (buttonSpacing * 2) + areaSize;
    int shortEdgePx = svgButtonSize;

    // clip selection to monitor
    Gdiplus::Rect selection;
    Gdiplus::Rect::Intersect(selection, screenBounds, Rect2Gdiplus(selectionRect));

    int bottomSpace = max(screenBounds.GetBottom() - selection.GetBottom(), 0) - minDistance;
    int rightSpace = max(screenBounds.GetRight() - selection.GetRight(), 0) - minDistance;
    int leftSpace = max(selection.GetLeft() - screenBounds.GetLeft(), 0) - minDistance;

    int indTop, indLeft;

    if (bottomSpace >= shortEdgePx)
    {
        vert = TRUE;
        indLeft = selection.GetLeft() + selection.Width / 2 - longEdgePx / 2;
        indTop = min(screenBounds.GetBottom(), selection.GetBottom() + maxDistance + shortEdgePx) - shortEdgePx;
    }
    else if (rightSpace >= shortEdgePx)
    {
        vert = FALSE;
        indLeft = min(screenBounds.GetRight(), selection.GetRight() + maxDistance + shortEdgePx) - shortEdgePx;
        indTop = selection.GetBottom() - longEdgePx;
    }
    else if (leftSpace >= shortEdgePx)
    {
        vert = FALSE;
        indLeft = max(selection.GetLeft() - maxDistance - shortEdgePx, 0);
        indTop = selection.GetBottom() - longEdgePx;
    }
    else // inside capture rect
    {
        vert = TRUE;
        indLeft = selection.GetLeft() + selection.Width / 2 - longEdgePx / 2;
        indTop = selection.GetBottom() - shortEdgePx - (maxDistance * 2);
    }

    int horizontalSize = (vert) ? longEdgePx : shortEdgePx;
    int verticalSize = (vert) ? shortEdgePx : longEdgePx;

    if (indLeft < screenBounds.GetLeft())
        indLeft = screenBounds.GetLeft();
    else if (indLeft + horizontalSize > screenBounds.GetRight())
        indLeft = screenBounds.GetRight() - horizontalSize;

    RECT desiredRect;
    desiredRect.left = indLeft;
    desiredRect.top = indTop;
    desiredRect.right = indLeft + horizontalSize;
    desiredRect.bottom = indTop + verticalSize;

    LONG* vchange = vert ? &desiredRect.left : &desiredRect.top;

    // area indicator
    buttonPos[NUM_SVG_BUTTONS].left = desiredRect.left;
    buttonPos[NUM_SVG_BUTTONS].top = desiredRect.top;
    buttonPos[NUM_SVG_BUTTONS].right = desiredRect.left + areaSize;
    buttonPos[NUM_SVG_BUTTONS].bottom = desiredRect.top + areaSize;
    *vchange += areaSize + buttonSpacing;

    for (int i = 0; i < NUM_SVG_BUTTONS; i++)
    {
        buttonPos[i].left = desiredRect.left;
        buttonPos[i].top = desiredRect.top;
        buttonPos[i].right = desiredRect.left + svgButtonSize;
        buttonPos[i].bottom = desiredRect.top + svgButtonSize;
        *vchange += svgButtonSize;
        if (i == 0) *vchange += buttonSpacing;
    }
}


DxScreenCapture::DxScreenCapture(captureArgs* options)
{
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    memcpy(&_options, options, sizeof(captureArgs));

    //_options = options;
    //windows = gcnew System::Collections::Generic::List<render_info>();
    //errors = gcnew System::Collections::Generic::List<System::Exception^>();
    //winmsgs = gcnew System::Collections::Generic::List<System::String^>();

    mainThread = (HANDLE)_beginthreadex(NULL, 0, MessagePumpProc, static_cast<LPVOID>(this), 0, &mainThreadId);

    //main = gcnew Thread(gcnew ThreadStart(this, &DxScreenCapture::RunMessagePump));
    //main->IsBackground = false;
    //main->SetApartmentState(ApartmentState::STA);
    //main->Priority = ThreadPriority::Highest;
    //main->Start();
}

DxScreenCapture::~DxScreenCapture()
{
    if (native != 0)
        Close(true);
}

unsigned int __stdcall DxScreenCapture::MessagePumpProc(void* lpParam)
{
    DxScreenCapture* pThis = static_cast<DxScreenCapture*>(lpParam);
    pThis->RunMessagePump();
    return 0;
}

void DxScreenCapture::RunMessagePump()
{
    // if we call any other API's beforehand which return scaled coordinates, this call will have no effect.
    // so to avoid that, this should always be the first thing run in this thread.
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    HINSTANCE hInstance = GetModuleHandle(0);

    wstring clsName = L"ClowdCaptureDX-" + std::to_wstring(clock());
    auto umClsName = clsName.c_str();

    try
    {
        native = new mc_window_native();
        native->t0 = std::chrono::high_resolution_clock::now();

        POINT pt;
        GetCursorPos(&pt);

        // misc stuff to create
        native->disposed = 0;
        native->screens = make_unique<Screens>();
        native->walker = make_unique<WindowWalker>(native->screens.get());
        native->frame.zoom = 1;
        native->frame.mouse = native->screens->ToWorkspacePt(pt);
        native->frame.tips = _options.tipsDisabled ? false : true;
        native->frame.animated = _options.animationDisabled ? false : true;
        native->frame.crosshair = true;

        // take screenshot
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        native->screenshot = BitmapEx::Make32bppDib(_vw, -_vh); // negative height to flip vertically for D2D1

        SetLastError(0);

        HDC screenDC = GetDC(HWND_DESKTOP);
        if (!BitBlt(native->screenshot->GetBitmapDC(), 0, 0, _vw, _vh, screenDC, _vx, _vy, SRCCOPY | CAPTUREBLT))
            HR_last_error();

        ReleaseDC(HWND_DESKTOP, screenDC);

        // get output devices
        native->monitors.clear();
        DxOutputDevice::EnumOutputs(native->monitors);

        // register wndproc
        //_del = gcnew WndProcDel(this, &DxScreenCapture::WndProc);
        //auto procPtr = Marshal::GetFunctionPointerForDelegate(_del);
        //auto proc = static_cast<WNDPROC>(procPtr.ToPointer());

        WNDCLASS wc = { };
        wc.lpfnWndProc = DxScreenCapture::WndProc;
        wc.hInstance = hInstance;
        wc.lpszClassName = umClsName;

        ATOM atom = RegisterClass(&wc);
        if (atom == 0)
            HR_last_error();

        native->t1 = std::chrono::high_resolution_clock::now();

        // capture desktop windows
        native->walker->ShapshotTopLevel();

        if (IsRectEmpty(&_options.initialRect))
        {
            FrameUpdateHoveredWindow(native->frame);
        }
        else
        {
            FrameMakeSelection(native->frame, _options.initialRect, false);    
        }

        native->t2 = std::chrono::high_resolution_clock::now();

        windows.reserve(native->monitors.size());

        // start rendering threads
        for (int i = 0; i < native->monitors.size(); i++)
        {
            // create a render thread for each monitor
            const DxDisplay& mon = native->monitors.at(i);
            auto& screen = native->screens->ScreenFromHMONITOR(mon.Monitor);
            const RECT& rcBounds = mon.DesktopCoordinates;
            auto bounds = Rect2Gdi(&rcBounds);

            HWND hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
                umClsName,
                L"ClowdCapture",
                WS_POPUP,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                0,
                0,
                hInstance,
                0
            );

            if (hwnd == 0)
                HR_last_error();

            if (screen.primary)
                primaryWindow = hwnd;

            SetWindowLongPtr(hwnd, GWL_USERDATA, reinterpret_cast<LONG_PTR>(this));

            int dwFlag = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &dwFlag, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, &dwFlag, sizeof(int));

            auto idx = windows.size();
            render_info& wi = windows.emplace_back();
            wi.idx = idx;
            wi.pParent = static_cast<LPVOID>(this);
            wi.eventSignal = CreateEvent(NULL, FALSE, TRUE, NULL);
            wi.hWnd = hwnd;
            wi.threadHandle = (HANDLE)_beginthreadex(NULL, 0, DxScreenCapture::RenderThreadProc, static_cast<LPVOID>(&wi), 0, &wi.threadId);

            //auto info = gcnew ParameterizedThreadStart(this, &DxScreenCapture::RunRenderThread);
            //auto thread = gcnew Thread(info);
            //thread->IsBackground = true;
            //render_info wi{}; // = gcnew WndInfo();
            //wi.hWnd = hwnd;
            //wi.thread = thread;
            //wi.signal = gcnew AutoResetEvent(true);
            //windows->Add(wi);
            //thread->Start(i);
            //int a = 5;
        }

        native->t3 = std::chrono::high_resolution_clock::now();

        // pump windows messages
        MSG msg;
        while (GetMessage(&msg, 0, 0, 0))
        {
            // can safely break here - cleanup happens after
            if (msg.message == WMEX_DESTROY) break;

            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
    }
    catch (System::Exception err)
    {
        errors.push_back(err);
    }

    // mark as disposed and wait for render threads to exit before we delete native
    native->disposed++;
    for (int i = 0; i < windows.size(); i++)
    {
        render_info& wi = windows[i];
        SetEvent(wi.eventSignal);
        WaitForSingleObject(wi.threadHandle, 5000); // wait for thread to end
        DestroyWindow(wi.hWnd);
    }

    UnregisterClass(umClsName, hInstance);
    delete native;
    native = 0;

    if (_options.lpfnDisposed)
    {
        if (errors.size() > 0)
        {
            auto msg = std::string(errors.front().what());
            _options.lpfnDisposed(s2ws(msg).c_str());
        }
        else
        {
            _options.lpfnDisposed(0);
        }
    }
}

unsigned int __stdcall DxScreenCapture::RenderThreadProc(void* lpParam)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    render_info* pInfo = static_cast<render_info*>(lpParam);
    DxScreenCapture* pThis = static_cast<DxScreenCapture*>(pInfo->pParent);

    try
    {
        //int idx = (int)boxed_idx;
        //auto wi = windows[idx];
        DxDisplay& mon = pThis->native->monitors.at(pInfo->idx);
        const ScreenInfo& myscreen = pThis->native->screens->ScreenFromHMONITOR(mon.Monitor);

        pThis->RunRenderLoop(*pInfo, mon, myscreen);
    }
    catch (System::Exception err)
    {
        // TODO, handle following responses from Present
        // DXGI_STATUS_OCCLUDED
        // DXGI_ERROR_DEVICE_REMOVED
        // DXGI_ERROR_DEVICE_RESET

        pThis->errors.push_back(err);
        pThis->Close();
    }

    return 0;
}

//void DxScreenCapture::RunRenderThread(Object^ boxed_idx)
//{
//    try
//    {
//        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
//        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
//
//        int idx = (int)boxed_idx;
//        auto wi = windows[idx];
//        DxDisplay& mon = native->monitors.at(idx);
//        const ScreenInfo& myscreen = native->screens->ScreenFromHMONITOR(mon.Monitor);
//
//        RunRenderLoop(wi, mon, myscreen);
//    }
//    catch (System::Exception^ err)
//    {
//        // TODO, handle following responses from Present
//        // DXGI_STATUS_OCCLUDED
//        // DXGI_ERROR_DEVICE_REMOVED
//        // DXGI_ERROR_DEVICE_RESET
//
//        errors->Add(err);
//        Close();
//    }
//}

void DxScreenCapture::RunRenderLoop(const render_info& wi, const DxDisplay& mon, const ScreenInfo& myscreen)
{
    auto myzoom = myscreen.dpi / BASE_DPI;
    bool shown = false;

    auto bounds = Rect2Gdi(&mon.DesktopCoordinates);
    int width = bounds.Width;
    int height = bounds.Height;
    float lineWidth = 1.0f;

    std::chrono::steady_clock::time_point trender;

    unique_ptr<DxOutputDevice> output = make_unique<DxOutputDevice>(wi.hWnd, mon);

    auto defaultMatrix = D2D1::Matrix3x2F::Identity();

    // text formats
    DxRef<IDWriteTextFormat> txtFmtSize, txtFmtDebug, txtInfo, txtInfoTitle, txtButtonLabel;
    output->CreateFontFormat(txtInfoTitle, 14 * myzoom, DWRITE_FONT_WEIGHT_BOLD, false);
    output->CreateFontFormat(txtInfo, 12 * myzoom, DWRITE_FONT_WEIGHT_NORMAL, true);
    output->CreateFontFormat(txtFmtSize, 14 * myzoom, DWRITE_FONT_WEIGHT_BOLD, false);
    output->CreateFontFormat(txtFmtDebug, 12 * myzoom, DWRITE_FONT_WEIGHT_NORMAL, true);
    output->CreateFontFormat(txtButtonLabel, 10 * myzoom, DWRITE_FONT_WEIGHT_DEMI_BOLD, false);

    // brushes
    DxRef<ID2D1SolidColorBrush> brushWhite, brushWhite70, brushWhite30, brushBlack, brushAccent, brushAccent70, brushOverlay30, brushOverlay50, brushOverlay70, brushCursorHovered, brushGray;
    output->CreateSolidBrush(brushWhite, 1, 1, 1);
    output->CreateSolidBrush(brushWhite70, 1, 1, 1, 0.7);
    output->CreateSolidBrush(brushWhite30, 1, 1, 1, 0.3);
    output->CreateSolidBrush(brushBlack, 0, 0, 0);
    output->CreateSolidBrush(brushAccent, _options.colorR / 255.0, _options.colorG / 255.0, _options.colorB / 255.0);
    output->CreateSolidBrush(brushAccent70, _options.colorR / 255.0, _options.colorG / 255.0, _options.colorB / 255.0, 0.7);
    output->CreateSolidBrush(brushOverlay30, 0, 0, 0, 0.3);
    output->CreateSolidBrush(brushOverlay50, 0, 0, 0, 0.5);
    output->CreateSolidBrush(brushOverlay70, 0, 0, 0, 0.7);
    output->CreateSolidBrush(brushGray, 0.216, 0.216, 0.216, 1);

    // strokes
    DxRef<ID2D1StrokeStyle> stroke8;
    output->CreateStrokeStyle(stroke8, 8, 0);

    // bitmaps
    DxRef<ID2D1Bitmap> bitmapColor;
    output->CreateBitmapFrom32bppGdiDib(bitmapColor, native->screenshot.get());

    // effects
    DxRef<ID2D1Effect> bitmapGray, bitmapBlurred;
    output->CreateEffect(bitmapGray, CLSID_D2D1Grayscale, bitmapColor);
    output->CreateEffect(bitmapBlurred, CLSID_D2D1GaussianBlur);
    bitmapBlurred->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, 2.0f);
    bitmapBlurred->SetValue(D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION, D2D1_DIRECTIONALBLUR_OPTIMIZATION_QUALITY);
    bitmapBlurred->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, D2D1_BORDER_MODE_HARD);
    bitmapBlurred->SetInputEffect(0, bitmapGray);

    // svg
    FLOAT svgIconSize = floor(UNSCALED_BUTTON_ICON_SIZE * myzoom);
    D2D1_SIZE_F svgSize{ svgIconSize, svgIconSize };
    auto buttonSvgs = make_unique<DxRef<ID2D1SvgDocument>[]>(NUM_SVG_BUTTONS);
    for (int i = 0; i < NUM_SVG_BUTTONS; i++)
    {
        auto& detail = captureButtonDetails[i];
        output->CreateSvgDocumentFromResource(buttonSvgs[i], svgSize, MAKEINTRESOURCE(detail.resourceId), L"SVG");
    }

    // other
    auto windowBitmaps = make_unique<DxRef<ID2D1Bitmap>[]>(native->walker->List()->size());
    auto windowBlended = make_unique<DxRef<ID2D1Bitmap1>[]>(native->walker->List()->size());
    auto colorCarousel = make_unique<DxRef<ID2D1SolidColorBrush>[]>(3);
    output->CreateSolidBrush(colorCarousel[0], 1, 0, 0);
    output->CreateSolidBrush(colorCarousel[1], 0, 1, 0);
    output->CreateSolidBrush(colorCarousel[2], 0, 0, 1);

    mc_frame_data data{};

    while (native->disposed < 1)
    {
        GetFrame(&data);

        if (!data.animated)
        {
            // wait for new window message before drawing
            WaitForSingleObject(wi.eventSignal, 1000);
        }

        ID2D1DeviceContext5* dc;
        output->BeginDraw(&dc);

        // BeginDraw can also wait, so get the latest frame data
        GetFrame(&data);

        // update hovered color
        auto hoveredColor = GetFrameHoveredColor(&data);
        output->CreateSolidBrush(brushCursorHovered, hoveredColor);

        double mx = data.mouse.x - bounds.X + _vx;
        double my = data.mouse.y - bounds.Y + _vy;

        const ScreenInfo& cursorscreen = native->screens->ScreenFromWorkspacePt(data.mouse);
        auto cursorzoom = cursorscreen.dpi / BASE_DPI;
        auto cursor_on_me = cursorscreen.hMonitor == mon.Monitor;

        auto zoomMatrix = data.zoom <= 1 ? defaultMatrix : D2D1::Matrix3x2F::Scale(
            D2D1::Size(data.zoom, data.zoom),
            D2D1::Point2F(mx, my));

        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
        dc->SetTransform(zoomMatrix);

        // if crosshair is on, we render all the goodies. if it's not, we just render the foreground image and nothing else.
        if (data.crosshair)
        {
            // grayscale background image
            D2D1_POINT_2F offset{};
            offset.x = _vx - bounds.X;
            offset.y = _vy - bounds.Y;
            dc->DrawImage(bitmapGray, offset, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
            dc->FillRectangle(D2D1::RectF(0, 0, width, height), brushOverlay30);

            // draw foreground image & border
            if (!IsRectEmpty(&data.selection))
            {
                // selection is currently relative to workspace
                offset.x += data.selection.left;
                offset.y += data.selection.top;
                D2D1_RECT_F sel{};
                sel.left = data.selection.left;
                sel.top = data.selection.top;
                sel.right = data.selection.right;
                sel.bottom = data.selection.bottom;

                // selection image
                NativeDib* wnd;
                int cropX, cropY;
                if (!_options.obstructedWindowDisabled && data.windowSelection.window != nullptr && data.windowSelection.window->GetBitmap(&wnd, &cropX, &cropY))
                {
                    const WindowInfo* swnd = data.windowSelection.window;

                    DxRef<ID2D1Bitmap>& widx = windowBitmaps[swnd->index];
                    DxRef<ID2D1Bitmap1>& wiblend = windowBlended[swnd->index];

                    // we have not yet cached this image, lets create it
                    if (!widx.has())
                    {
                        output->CreateBitmapFrom32bppGdiDib(widx, wnd);

                        DxRef<ID2D1Effect> widxtransparent;
                        output->CreateEffect(widxtransparent, CLSID_D2D1Opacity, widx);
                        widxtransparent->SetValue(D2D1_OPACITY_PROP_OPACITY, 0.3f);

                        D2D1_BITMAP_PROPERTIES1 bitmapProperties = D2D1::BitmapProperties1(
                            D2D1_BITMAP_OPTIONS_TARGET,
                            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED)
                        );
                        auto blsi = D2D1::SizeU(RECT_WIDTH(swnd->rcWindow), RECT_HEIGHT(swnd->rcWindow));
                        HR(dc->CreateBitmap(blsi, 0, 0, bitmapProperties, wiblend.put()));

                        D2D1_MATRIX_3X2_F oldTransform;
                        DxRef<ID2D1Image> oldTarget;
                        dc->GetTarget(oldTarget.put());
                        dc->GetTransform(&oldTransform);

                        dc->SetTarget(wiblend);
                        dc->SetTransform(defaultMatrix);

                        dc->DrawImage(widx);
                        for each (auto & intersect in swnd->obstructionRects)
                        {
                            auto pt = D2D1::Point2F(intersect.left - swnd->rcWorkspace.left + cropX, intersect.top - swnd->rcWorkspace.top + cropY);
                            dc->DrawImage(bitmapBlurred, pt, Rect2D2D1(&intersect), D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
                        }
                        dc->DrawImage(widxtransparent);

                        dc->SetTarget(oldTarget);
                        dc->SetTransform(oldTransform);
                    }

                    D2D1_RECT_F crop{};
                    crop.left = cropX + (sel.left - swnd->rcWorkspace.left);
                    crop.top = cropY + (sel.top - swnd->rcWorkspace.top);
                    crop.right = crop.left + RECT_WIDTH(sel);
                    crop.bottom = crop.top + RECT_HEIGHT(sel);

                    if (data.captured)
                    {
                        dc->DrawImage(widx, offset, crop, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
                    }
                    else
                    {
                        dc->DrawImage(wiblend, offset, crop, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
                    }
                }
                else
                {
                    dc->DrawImage(bitmapColor, offset, sel, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
                }

                // draw ALL detected child rectangles (if debugging)
                if (!data.captured && data.debugging && data.windowSelection.window != nullptr)
                {
                    for (int i = 0; i < data.windowSelection.cRects; i++)
                    {
                        // draw outline
                        auto& rect = data.windowSelection.rects[i];
                        auto d2rect = TranslateFromWorkspaceToScreen(_vx, _vy, mon.DesktopCoordinates, rect.workspaceRect);
                        dc->DrawRectangle(d2rect, colorCarousel[0], 2 / data.zoom);
                    }
                }

                // translate selection relative to this display
                sel = TranslateFromWorkspaceToScreen(_vx, _vy, mon.DesktopCoordinates, sel);


                unsigned long milliseconds_since_epoch =
                    std::chrono::duration_cast<std::chrono::milliseconds>
                    (std::chrono::system_clock::now().time_since_epoch()).count();

                double progressToNextSecond = (milliseconds_since_epoch % 1000) / 1000.0;

                //double progress = System::DateTime::Now.Millisecond / 1000.0;
                double offset = progressToNextSecond * (8 * 2);

                DxRef<ID2D1StrokeStyle> borderdash;
                output->CreateStrokeStyle(borderdash, 8, -offset);

                // draw animated(?) selection border
                if (data.animated)
                {
                    dc->DrawRectangle(sel, brushWhite, 2 / data.zoom);
                    dc->DrawRectangle(sel, brushAccent, 2 / data.zoom, borderdash);
                }
                else
                {
                    dc->DrawRectangle(sel, brushAccent, 2 / data.zoom);
                }

                // area indicator
                if (cursor_on_me && !data.captured) // only draw if cursor on this display
                {
                    dc->SetTransform(defaultMatrix);
                    wstring txt = to_wstring(RECT_WIDTH(data.selection)) + L" \x00D7 " + to_wstring(RECT_HEIGHT(data.selection));

                    DWRITE_TEXT_METRICS metrics;
                    DxRef<IDWriteTextLayout> layout;
                    output->CreateFontLayout(layout, txtFmtSize, txt, &metrics, 300, 300);

                    double padding = UNSCALED_AREA_PADDING * myzoom;
                    double area_width = metrics.width + (padding * 2);
                    double area_height = metrics.height + padding;

                    // clip selection to current monitor
                    RECT clip{ 0, 0, width, height };
                    ClipRectBy(&sel, &clip);
                    int sel_width = RECT_WIDTH(sel);
                    int sel_height = RECT_HEIGHT(sel);

                    // draw only if it fits inside
                    if (sel_width * data.zoom > area_width + padding && sel_height * data.zoom > area_height + padding)
                    {
                        D2D1_POINT_2F origin;
                        origin.x = sel.left + (sel_width / 2.0);// -(area_width / 2.0);
                        origin.y = sel.bottom;// -(padding / 2.0) - area_height;

                        if (data.zoom > 1)
                            origin = zoomMatrix.TransformPoint(origin);

                        origin.x = origin.x - area_width / 2.0;
                        origin.y = origin.y - (padding / 2.0) - area_height;

                        // clip to display
                        origin.x = min(max(origin.x, 0), width - area_width);
                        origin.y = min(max(origin.y, 0), height - area_height);

                        D2D1_ROUNDED_RECT round;
                        round.rect.left = origin.x;
                        round.rect.top = origin.y;
                        round.rect.right = origin.x + area_width;
                        round.rect.bottom = origin.y + area_height;
                        round.radiusX = round.radiusY = (area_height / 2);

                        dc->FillRoundedRectangle(round, brushWhite);
                        dc->DrawRoundedRectangle(round, brushAccent, 2);

                        D2D1_POINT_2F txtpt;
                        txtpt.x = origin.x + padding;
                        txtpt.y = origin.y + (padding / 2.0);
                        dc->DrawTextLayout(txtpt, layout, brushBlack);
                    }
                }
            }

            dc->SetTransform(defaultMatrix);

            // draw cursor
            if (!data.captured)
            {
                float cx = (int)floor(mx) + 0.5f;
                float cy = (int)floor(my) + 0.5f;
                auto pt_x1 = D2D1::Point2F(cx, 0);
                auto pt_x2 = D2D1::Point2F(cx, height);
                auto pt_y1 = D2D1::Point2F(0, cy);
                auto pt_y2 = D2D1::Point2F(width, cy);
                dc->DrawLine(pt_x1, pt_x2, brushWhite, lineWidth);
                dc->DrawLine(pt_x1, pt_x2, brushBlack, lineWidth, stroke8);
                dc->DrawLine(pt_y1, pt_y2, brushWhite, lineWidth);
                dc->DrawLine(pt_y1, pt_y2, brushBlack, lineWidth, stroke8);
                int chunk = cursorzoom * UNSCALED_CURSOR_PART_LENGTH;
                int chunk2 = chunk * 2;
                double wide = 5 * cursorzoom;
                int wideInt = 0;
                // we want to make wideInt an odd number if our crosshair is an odd number 
                // of pixels so it will always be pixel sharp
                if (floor(wide) / 2 != 0) // number is odd
                    wideInt = (int)floor(wide);
                else // number is even
                    wideInt = (int)ceil(wide);
                wideInt = min(wideInt, 10);
                dc->DrawLine(D2D1::Point2F(cx, cy - chunk), D2D1::Point2F(cx, cy + chunk), brushAccent, lineWidth);
                dc->DrawLine(D2D1::Point2F(cx - chunk, cy), D2D1::Point2F(cx + chunk, cy), brushAccent, lineWidth);
                dc->DrawLine(D2D1::Point2F(cx, cy - chunk), D2D1::Point2F(cx, cy - chunk2), brushAccent, wideInt);
                dc->DrawLine(D2D1::Point2F(cx, cy + chunk), D2D1::Point2F(cx, cy + chunk2), brushAccent, wideInt);
                dc->DrawLine(D2D1::Point2F(cx - chunk, cy), D2D1::Point2F(cx - chunk2, cy), brushAccent, wideInt);
                dc->DrawLine(D2D1::Point2F(cx + chunk, cy), D2D1::Point2F(cx + chunk2, cy), brushAccent, wideInt);
            }

            // draw tips panel
            if (myscreen.primary && !data.captured && data.tips && !data.mouseDown)
            {
                // create text
                wstring txtTips
                    = wstring(L"-   Scroll to zoom!\n")
                    + L"W   " + (data.windowSelection.window == nullptr ? L"n/a" : L"Select '" + data.windowSelection.window->caption) + L"'\n"
                    + L"F   Select monitor '" + cursorscreen.name + L"'\n"
                    + L"A   Select all monitors";
                wstring txtTips2 = L"D   Toggle debug stats\nT   Toggle this panel\nQ   Toggle cursor/crosshair";
                wstring txtSpace = L" ";
                wstring txtColorHeader = L"H";
                wstring txtColorDetail = to_hex(hoveredColor) + L"\nrgb(" + to_wstring(hoveredColor.GetR()) + L", " + to_wstring(hoveredColor.GetG()) + L", " + to_wstring(hoveredColor.GetB()) + L")";
                wstring txtTitle = L"Tips & Hotkeys";

                // create resources
                DWRITE_TEXT_METRICS metricsTips, metricsTips2, metricsSpace, metricsColorHeader, metricsColorDetail, metricsTitle;
                DxRef<IDWriteTextLayout> layoutTips, layoutTips2, layoutSpace, layoutColorHeader, layoutColorDetail, layoutTitle;
                output->CreateFontLayout(layoutTips, txtInfo, txtTips, &metricsTips, DEBUGBOX_SIZE, DEBUGBOX_SIZE);
                output->CreateFontLayout(layoutTips2, txtInfo, txtTips2, &metricsTips2, DEBUGBOX_SIZE, DEBUGBOX_SIZE);
                output->CreateFontLayout(layoutSpace, txtInfo, txtSpace, &metricsSpace, DEBUGBOX_SIZE, DEBUGBOX_SIZE);
                output->CreateFontLayout(layoutColorHeader, txtInfo, txtColorHeader, &metricsColorHeader, DEBUGBOX_SIZE, DEBUGBOX_SIZE);
                output->CreateFontLayout(layoutColorDetail, txtInfo, txtColorDetail, &metricsColorDetail, DEBUGBOX_SIZE, DEBUGBOX_SIZE);
                output->CreateFontLayout(layoutTitle, txtInfoTitle, txtTitle, &metricsTitle, DEBUGBOX_SIZE, DEBUGBOX_SIZE);

                // used to help with padding / layout
                auto paddingHalf = 10 * myzoom;
                auto padding = paddingHalf * 2;
                FLOAT space1 = metricsSpace.widthIncludingTrailingWhitespace;
                FLOAT space3 = space1 * 3;
                FLOAT panelWidth = max(max(metricsTips.width, metricsTips2.width) + (padding * 2), 450);
                FLOAT panelHeight = metricsTips.height + metricsTips2.height + (metricsColorHeader.height * 2) + (padding * 2);

                // calculate tips position on right side. if cursor too close, move to left side.
                auto tr = D2D1::RectF(floor(width - DEBUGBOX_MARGIN - panelWidth), floor(height - DEBUGBOX_MARGIN - panelHeight), ceil(width - DEBUGBOX_MARGIN), ceil(height - DEBUGBOX_MARGIN));
                if (mx > tr.left - DEBUGBOX_MARGIN * 2 && my > tr.top - DEBUGBOX_MARGIN * 2)
                {
                    tr = D2D1::RectF(DEBUGBOX_MARGIN, tr.top, DEBUGBOX_MARGIN + panelWidth, tr.bottom);
                }

                // draw background / shadows
                tr.top -= (metricsTitle.height + padding);
                dc->FillRectangle(tr, brushOverlay30);
                auto shadowRight = D2D1::RectF(tr.right, tr.top + paddingHalf, tr.right + paddingHalf, tr.bottom + paddingHalf);
                auto shadowBottom = D2D1::RectF(tr.left + paddingHalf, tr.bottom, tr.right, tr.bottom + paddingHalf);
                dc->FillRectangle(shadowRight, brushOverlay30);
                dc->FillRectangle(shadowBottom, brushOverlay30);
                tr.top += (metricsTitle.height + padding);
                dc->FillRectangle(tr, brushWhite70);

                // draw txtTips
                FLOAT y = tr.top + padding;
                dc->DrawTextLayout(D2D1::Point2F(tr.left + padding, y), layoutTips, brushBlack);
                y += metricsTips.height;

                // draw txtColorHeader
                dc->DrawTextLayout(D2D1::Point2F(tr.left + padding, y), layoutColorHeader, brushBlack);

                // draw hovered color box
                auto clr = D2D1::RectF(
                    ceil(tr.left + padding + space3 + metricsColorHeader.width),
                    ceil(y),
                    floor(tr.left + padding + space3 + metricsColorHeader.width + (metricsColorHeader.height * 2)),
                    floor(y + (metricsColorHeader.height * 2))
                );
                FLOAT detailX = clr.right + space1;
                dc->FillRectangle(clr, brushBlack);
                clr.left += 1.0f;
                clr.top += 1.0f;
                clr.right -= 1.0f;
                clr.bottom -= 1.0f;
                dc->FillRectangle(clr, brushCursorHovered);

                // draw txtColorDetail
                dc->DrawTextLayout(D2D1::Point2F(detailX, y), layoutColorDetail, brushBlack);
                y += metricsColorDetail.height;

                // draw txtTips2
                dc->DrawTextLayout(D2D1::Point2F(tr.left + padding, y), layoutTips2, brushBlack);

                // draw title background
                tr.bottom = tr.top;
                tr.top -= (metricsTitle.height + padding);
                dc->FillRectangle(tr, brushAccent70);

                // draw txtTitle
                dc->DrawTextLayout(D2D1::Point2F(tr.left + (RECT_WIDTH(tr) / 2) - (metricsTitle.width / 2), tr.top + (padding / 2)), layoutTitle, brushWhite70);
            }

            // draw button panel if image is captured
            if (data.captured)
            {
                for (int i = 0; i < NUM_SVG_BUTTONS; i++)
                {
                    dc->SetTransform(defaultMatrix);
                    auto& detail = captureButtonDetails[i];
                    auto& svg = buttonSvgs[i];
                    auto& position = data.buttonPositions[i];

                    auto br = TranslateFromWorkspaceToScreen(_vx, _vy, mon.DesktopCoordinates, position);
                    auto bw = RECT_WIDTH(br);
                    auto bh = RECT_WIDTH(br);

                    if (detail.primary)
                    {
                        dc->FillRectangle(br, brushAccent);
                    }
                    else
                    {
                        dc->FillRectangle(br, brushGray);
                    }

                    // draw hover color if mouse is inside
                    if (mx > br.left && my > br.top && mx < br.right && my < br.bottom) {
                        dc->FillRectangle(br, brushWhite30);
                    }

                    // draw text
                    DWRITE_TEXT_METRICS metricsText;
                    DxRef<IDWriteTextLayout> layoutText;
                    output->CreateFontLayout(layoutText, txtButtonLabel, detail.text, &metricsText, bw, bh);
                    layoutText->SetUnderline(TRUE, DWRITE_TEXT_RANGE{ detail.underlineIndex, 1 });
                    dc->DrawTextLayout(D2D1::Point2F(br.left + (bw / 2) - (metricsText.width / 2), br.bottom - metricsText.height - (5 * myzoom)), layoutText, brushWhite);

                    // draw svg
                    dc->SetTransform(D2D1::Matrix3x2F::Translation(
                        br.left + (bw / 2) - (svgIconSize / 2),
                        br.top + ((bh - metricsText.height) / 2) - (svgIconSize / 2)));
                    dc->DrawSvgDocument(svg);
                    dc->SetTransform(defaultMatrix);
                }

                // draw area indicator as last button
                int buttonSpacing = (int)ceil(3 * myzoom);
                auto& lastButton = data.buttonPositions[NUM_SVG_BUTTONS];
                auto br = TranslateFromWorkspaceToScreen(_vx, _vy, mon.DesktopCoordinates, lastButton);
                auto bw = RECT_WIDTH(br);
                auto bh = RECT_WIDTH(br);
                dc->FillRectangle(br, brushGray);

                DWRITE_TEXT_METRICS metricsWidth, metricsHeight, metricsX;
                DxRef<IDWriteTextLayout> layoutWidth, layoutHeight, layoutX;
                output->CreateFontLayout(layoutWidth, txtInfo, to_wstring(RECT_WIDTH(data.selection)), &metricsWidth, bw, bh);
                output->CreateFontLayout(layoutHeight, txtInfo, to_wstring(RECT_HEIGHT(data.selection)), &metricsHeight, bw, bh);
                output->CreateFontLayout(layoutX, txtInfo, L"\x00D7", &metricsX, bw, bh);
                dc->DrawTextLayout(D2D1::Point2F(br.left + (bw / 2) - (metricsWidth.width / 2), br.top + (bh / 4) - (metricsWidth.height / 2)), layoutWidth, brushWhite);
                dc->DrawTextLayout(D2D1::Point2F(br.left + (bw / 2) - (metricsHeight.width / 2), br.top + (bh / 1.34) - (metricsHeight.height / 2)), layoutHeight, brushWhite);
                dc->DrawTextLayout(D2D1::Point2F(br.left + (bw / 2) - (metricsX.width / 2), br.top + (bh / 2) - (metricsX.height / 2)), layoutX, brushWhite70);

                float line = 2;
                float edge = floor(bw / 3);
                dc->FillRectangle(D2D1_RECT_F{ br.left, br.top, br.left + edge, br.top + line }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.left, br.top, br.left + line, br.top + edge }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.right - edge, br.top, br.right, br.top + line }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.right - line, br.top, br.right, br.top + edge }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.left, br.bottom - line, br.left + edge, br.bottom }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.left, br.bottom - edge, br.left + line, br.bottom }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.right - edge, br.bottom - line, br.right, br.bottom }, brushWhite);
                dc->FillRectangle(D2D1_RECT_F{ br.right - line, br.bottom - edge, br.right, br.bottom }, brushWhite);
            }

        }
        // if crosshair is off, forget everything else we just only draw a color image
        else
        {
            D2D1_POINT_2F offset{};
            offset.x = _vx - bounds.X;
            offset.y = _vy - bounds.Y;
            dc->DrawImage(bitmapColor, offset, D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_BOUNDED_SOURCE_COPY);
        }

        dc->SetTransform(defaultMatrix);
        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE); // slightly faster, and important stuff is done

        // draw debug info
        if (data.debugging)
        {
            // draw monitor rendering stats (on each display)
            wstring detail = to_wstring(myscreen.index) + L": " + myscreen.name;
            if (myscreen.primary) detail += L"  (PRIMARY)";

            detail += L"\nadapter: " + wstring(mon.AdapterDescription) + L"\n"
                + L"dpi: " + to_wstring(myscreen.dpi) + L"\n"
                + L"pos: " + Rect2String(&myscreen.realBounds) + L"\n"
                + L"time_to_render: " + to_wfmt(to_time_ms(native->t0, trender)) + L"ms\n\n"
                + output->GetDebugText();

            auto dbg_pad = 20 * myzoom;
            DWRITE_TEXT_METRICS metrics;
            DxRef<IDWriteTextLayout> layout;
            output->CreateFontLayout(layout, txtFmtDebug, detail, &metrics, width / 2, height - (DEBUGBOX_MARGIN * 2));
            dc->FillRectangle(D2D1::RectF(DEBUGBOX_MARGIN - dbg_pad, DEBUGBOX_MARGIN - dbg_pad, DEBUGBOX_MARGIN + metrics.width + dbg_pad, DEBUGBOX_MARGIN + metrics.height + dbg_pad), brushOverlay70);
            dc->DrawTextLayout(D2D1::Point2F(DEBUGBOX_MARGIN, DEBUGBOX_MARGIN), layout, brushWhite);

            // draw primary debug panel only on the display intersecting the cursor
            if (cursor_on_me)
            {
                auto df = L"%06.2f";
                wstring detail2 = L"startup: " + to_wfmt(to_time_ms(native->t0, trender), df) + L"ms total"
                    + L"\n  - " + to_wfmt(to_time_ms(native->t0, native->t1), df) + L"ms (initialize)"
                    + L"\n  - " + to_wfmt(to_time_ms(native->t1, native->t2), df) + L"ms (desktop search)"
                    + L"\n  - " + to_wfmt(to_time_ms(native->t2, native->t3), df) + L"ms (window create)"
                    + L"\n  - " + to_wfmt(to_time_ms(native->t3, trender), df) + L"ms (" + wstring(mon.DeviceName) + L")"
                    + L"\n"
                    + L"\nzoom: " + to_wstring(data.zoom)
                    + L"\nmouse: " + to_wfmt(data.mouse.x) + L", " + to_wfmt(data.mouse.y)
                    + L"\ncolor: rgb(" + to_wstring(hoveredColor.GetR()) + L", " + to_wstring(hoveredColor.GetG()) + L", " + to_wstring(hoveredColor.GetB()) + L")"
                    + L"\ndragging: " + (data.dragging ? L"true" : L"false")
                    + L"\ncaptured: " + (data.captured ? L"true" : L"false")
                    + L"\nselect: " + Rect2String(&data.selection);

                if (data.windowSelection.window != nullptr)
                {
                    auto wnd = data.windowSelection.window;
                    detail2
                        += L"\n\nwnd_title: " + wnd->caption
                        + L"\nwnd_class: " + wnd->className
                        + L"\nwnd_obstructed: " + (wnd->obstructed ? L"true" : L"false")
                        + L"\nwnd_captured: " + (wnd->captured ? L"true" : L"false")
                        + L"\nwnd_capture_time: " + to_wfmt(wnd->GetTimeToRender()) + L"ms"
                        + L"\nwnd_bounds: " + Rect2String(&wnd->rcWindow)
                        + L"\nwnd_workspace: " + Rect2String(&wnd->rcWorkspace)
                        + L"\n\nwnd_style: " + GetWindowStyleText(wnd->style)
                        + L"\n\nwnd_ex_style: " + GetWindowStyleExtendedText(wnd->exStyle);
                }

                output->CreateFontLayout(layout, txtFmtDebug, detail2, &metrics, DEBUGBOX_SIZE, height / 2);

                auto pddb = D2D1::RectF(
                    width - DEBUGBOX_MARGIN - metrics.width - dbg_pad,
                    DEBUGBOX_MARGIN - dbg_pad,
                    width - DEBUGBOX_MARGIN + dbg_pad,
                    DEBUGBOX_MARGIN + metrics.height + dbg_pad
                );
                dc->FillRectangle(pddb, brushOverlay70);
                dc->DrawTextLayout(D2D1::Point2F(width - DEBUGBOX_MARGIN - metrics.width, DEBUGBOX_MARGIN), layout, brushWhite);
            }

            // draw detected child window info
            if (!data.captured && data.windowSelection.window != nullptr)
            {
                auto txtBounds = TranslateFromWorkspaceToScreen(_vx, _vy, mon.DesktopCoordinates, data.windowSelection.window->rcWorkspace);
                txtBounds.top += 20;
                txtBounds.left += 20;
                txtBounds.right -= 20;
                txtBounds.bottom -= 20;
                wchar_t cls[128];
                for (int i = 0; i < data.windowSelection.cRects; i++)
                {
                    auto& rect = data.windowSelection.rects[i];

                    GetClassName(rect.hWnd, cls, 128);
                    auto debugTxt = to_wstring(i) + L": " + wstring(cls) + L" // " + GetWindowStyleText(rect.style) + L" | " + GetWindowStyleExtendedText(rect.exStyle);

                    wstring reason(rect.reason);
                    if (reason.size() > 0)
                        debugTxt += L"\n" + reason;

                    DWRITE_TEXT_METRICS metrics;
                    DxRef<IDWriteTextLayout> layout;
                    output->CreateFontLayout(layout, txtFmtDebug, debugTxt, &metrics, RECT_WIDTH(txtBounds), RECT_HEIGHT(txtBounds));

                    dc->FillRectangle(D2D1::RectF(txtBounds.left, txtBounds.top, txtBounds.left + metrics.width, txtBounds.top + metrics.height), brushOverlay50);
                    dc->DrawTextLayout(D2D1::Point2F(txtBounds.left, txtBounds.top), layout, rect.visible ? colorCarousel[1] : colorCarousel[0], D2D1_DRAW_TEXT_OPTIONS_CLIP);

                    txtBounds.top += (metrics.height + 20);
                }
            }
        }

        output->EndDraw();

        // show this window if it has not yet been shown. we only do this after drawing to mitigate flicker.
        if (!shown)
        {
            shown = true;
            ShowWindow(wi.hWnd, SW_SHOWNOACTIVATE);
            if (myscreen.primary)
            {
                // we have multiple windows, only want one to take focus
                SetForegroundWindow(wi.hWnd);
                SetActiveWindow(wi.hWnd);
            }

            trender = std::chrono::high_resolution_clock::now();
        }
    }
}

LRESULT DxScreenCapture::WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    //auto wmststr = GetWndMsgTxt(msg);
    //auto clrstr = msclr::interop::marshal_as<System::String^>(wmststr);
    //winmsgs->Add(clrstr + " (" + LOWORD(lParam) + ", " + HIWORD(lParam) + ")");

    DxScreenCapture* pInstance = reinterpret_cast<DxScreenCapture*>(GetWindowLongPtr(hWnd, GWL_USERDATA));
    if (pInstance == NULL) {
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    return pInstance->WndProcImpl(hWnd, msg, wParam, lParam);
}

LRESULT DxScreenCapture::WndProcImpl(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {

    case WMEX_RESET:
    {
        SetForegroundWindow(hWnd);
        mc_frame_data data{};
        GetFrame(&data);
        FrameMakeSelection(data, {}, false);
        SetFrame(&data);
        return 0;
    }

    case WM_PAINT:
    {
        ValidateRect(hWnd, NULL);

        // capture hidden/obstructed windows
        // it doesn't matter if we do this more than once, it's a nop
        auto wli = native->walker->List();
        auto wlilen = wli->size();
        for (int i = 0; i < wlilen; i++)
        {
            auto& wi = wli->at(i);
            wi.PrintImage();
        }

        return 0;
    }

    case WM_ERASEBKGND:
    {
        return TRUE;
    }

    case WM_NCHITTEST:
    {
        return HTCLIENT;
    }

    case WM_SETCURSOR:
    {
        mc_frame_data data{};
        GetFrame(&data);
        FrameSetCursor(data);
        return TRUE;
    }

    case WM_ACTIVATE:
    {
        mc_frame_data data{};
        GetFrame(&data);

        if (!data.captured)
        {
            if (LOWORD(wParam) > 0) // this window is being activated
            {
                if (data.mouseAnchored) return 0;
                data.mouseAnchored = true;
                data.animated = _options.animationDisabled ? false : true;
                native->screens->MouseAnchorStart(data.mouse);
                SetFrame(&data);
                //winmsgs->Add("== STARTED VIRTUAL MOUSE CAPTURE");
            }
            else // this window is being deactivated
            {
                if (!data.mouseAnchored) return 0;
                data.mouseAnchored = false;
                data.animated = false;
                native->screens->MouseAnchorStop(data.mouse);
                SetFrame(&data);
                //winmsgs->Add("== ENDED VIRTUAL MOUSE CAPTURE");
            }
        }

        return 0;
    }

    case WM_MOUSEACTIVATE:
    {
        mc_frame_data data{};
        GetFrame(&data);

        if (data.captured)
        {
            return MA_ACTIVATE;
        }
        else
        {
            // Activates the window, and discards the mouse message.
            return MA_ACTIVATEANDEAT;
        }
    }

    case WM_KEYDOWN:
    {
        mc_frame_data data{};
        GetFrame(&data);
        bool save = true;

        switch (wParam)
        {

        case VK_LEFT:
        {
            int modifier = (GetKeyState(VK_SHIFT) & 0x8000) ? 10 : 1;
            auto& sel = data.selection;
            if (data.captured)
                FrameMakeSelection(data, RECT{ sel.left - modifier, sel.top, sel.right - modifier, sel.bottom }, false);
            break;
        }

        case VK_UP:
        {
            int modifier = (GetKeyState(VK_SHIFT) & 0x8000) ? 10 : 1;
            auto& sel = data.selection;
            if (data.captured)
                FrameMakeSelection(data, RECT{ sel.left, sel.top - modifier, sel.right, sel.bottom - modifier }, false);
            break;
        }

        case VK_RIGHT:
        {
            int modifier = (GetKeyState(VK_SHIFT) & 0x8000) ? 10 : 1;
            auto& sel = data.selection;
            if (data.captured)
                FrameMakeSelection(data, RECT{ sel.left + modifier, sel.top, sel.right + modifier, sel.bottom }, false);
            break;
        }

        case VK_DOWN:
        {
            int modifier = (GetKeyState(VK_SHIFT) & 0x8000) ? 10 : 1;
            auto& sel = data.selection;
            if (data.captured)
                FrameMakeSelection(data, RECT{ sel.left, sel.top + modifier, sel.right, sel.bottom + modifier }, false);
            break;
        }

        case 0x41: // A
        {
            if (!data.captured)
                FrameMakeSelection(data, native->screens->WorkspaceBounds(), false);
            break;
        }

        case VK_INSERT:
        case 0x43: // C
        {
            if (data.captured)
            {
                WriteToClipboard();
                Close();
            }
            break;
        }

        case 0x44: // D
        {
            data.debugging = data.debugging ? false : true;
            break;
        }

        case 0x46: // F
        {
            const ScreenInfo& scr = native->screens->ScreenFromWorkspacePt(data.mouse);
            if (!data.captured)
                FrameMakeSelection(data, scr.workspaceBounds, false);
            break;
        }

        case 0x48: // H
        {
            if (!data.captured && _options.lpfnColorCapture)
            {
                auto color = GetFrameHoveredColor(&data);
                _options.lpfnColorCapture(color.GetR(), color.GetG(), color.GetB());
                Close();
            }
            break;
        }

        case 0x51: // Q
        {
            if (!data.captured)
                data.crosshair = data.crosshair ? false : true;
            break;
        }

        case 0x52: // R
        {
            FrameMakeSelection(data, {}, false);
            FrameUpdateHoveredWindow(data);
            break;
        }

        case 0x54: // T
        {
            if (!data.captured)
                data.tips = data.tips ? false : true;
            break;
        }

        case VK_RETURN:
        case 0x45: // E
        case 0x50: // P
        case 0x53: // S
        case 0x55: // U
        {
            if (data.captured && _options.lpfnSessionCapture)
            {
                CaptureType type;
                if (wParam == 0x55) type = CaptureType::Upload;
                else if (wParam == 0x53) type = CaptureType::Save;
                else type = CaptureType::Photo;

                auto sessionJson = SaveSession(_options.sessionDirectory, _options.createdUtc);
                _options.lpfnSessionCapture(sessionJson.c_str(), type);
                Close();
            }
            break;
        }

        case 0x56: // V
        {
            if (data.captured && _options.lpfnVideoCapture)
            {
                RECT sel;
                GetSelectionRect(sel);
                _options.lpfnVideoCapture(sel);
                Close();
            }
            break;
        }

        case 0x57: // W
        {
            if (!data.captured && data.windowSelection.window != nullptr)
                FrameMakeSelection(data, data.windowSelection.window->rcWorkspace, true);
            break;
        }

        case 0x58: // X
        case VK_ESCAPE:
        case VK_F4:
        {
            Close();
            break;
        }

        default:
        {
            save = false;
            break;
        }

        }

        if (save)
        {
            SetFrame(&data);
        }

        return 0;
    }

    case WM_POINTERDOWN:
    case WM_POINTERUP:
    case WM_POINTERENTER:
    case WM_POINTERLEAVE:
    case WM_POINTERUPDATE:
    case WM_POINTERWHEEL:
    case WM_POINTERHWHEEL:
    case WM_POINTERCAPTURECHANGED:
    {
        // TODO touch support. 
        // for now, disable touch turning into mouse moves as this breaks our mouse virtualisation 
        return 0;
    }

    case WM_LBUTTONDOWN:
    {
        mc_frame_data data{};
        GetFrame(&data);

        if (!data.crosshair)
            return 0; // short-circuit if no cursor

        // we're clicking a button?
        if (data.captured && data.hittestBtn >= 0)
        {
            auto& detail = captureButtonDetails[data.hittestBtn];
            WndProcImpl(hWnd, WM_KEYDOWN, detail.vKeyCode, 0);
            return 0;
        }

        SetCapture(hWnd);

        data.dragging = false;
        data.mouseDown = true;
        data.mouseDownPt = data.mouse;

        SetFrame(&data);

        return 0;
    }

    case WM_LBUTTONUP:
    {
        ReleaseCapture();
        mc_frame_data data{};
        GetFrame(&data);

        if (data.mouseDown)
        {
            FrameMakeSelection(data, data.selection, true);
            SetFrame(&data);
        }
        return 0;
    }

    case WM_MOUSEMOVE:
    {
        mc_frame_data data{};
        GetFrame(&data);

        POINT syspt;
        GetCursorPos(&syspt);

        // update virtual mouse position
        if (data.mouseAnchored && !native->screens->IsAnchorPt(syspt))
        {
            native->screens->MouseAnchorUpdate(data.mouse, syspt, data.zoom);
        }
        else if (data.captured)
        {
            data.mouse = native->screens->ToWorkspacePt(syspt);
        }

        double dpizoom = native->screens->ScreenFromWorkspacePt(data.mouse).dpi / BASE_DPI;
        POINTFF pt = data.mouse;
        POINT mpt = { (int)floor(data.mouse.x), (int)floor(data.mouse.y) };

        // update selection
        if (data.captured)
        {
            if (data.mouseDown)
            {
                // move selection
                if (data.hittest != HTCLIENT)
                {
                    // we're resizing or moving the selection, we should cancel the window selection
                    native->walker->ResetHitResult(&data.windowSelection);
                }

                bool updateButtonPos = true;

                switch (data.hittest)
                {

                case HTTOPLEFT:
                {
                    Xy12Rect(&data.selection, pt.x, pt.y, data.selection.right, data.selection.bottom);
                    break;
                }
                case HTTOP:
                {
                    Xy12Rect(&data.selection, data.selection.left, pt.y, data.selection.right, data.selection.bottom);
                    break;
                }
                case HTTOPRIGHT:
                {
                    Xy12Rect(&data.selection, data.selection.left, pt.y, pt.x, data.selection.bottom);
                    break;
                }
                case HTRIGHT:
                {
                    Xy12Rect(&data.selection, data.selection.left, data.selection.top, pt.x, data.selection.bottom);
                    break;
                }
                case HTBOTTOMRIGHT:
                {
                    Xy12Rect(&data.selection, data.selection.left, data.selection.top, pt.x, pt.y);
                    break;
                }
                case HTBOTTOM:
                {
                    Xy12Rect(&data.selection, data.selection.left, data.selection.top, data.selection.right, pt.y);
                    break;
                }
                case HTBOTTOMLEFT:
                {
                    Xy12Rect(&data.selection, pt.x, data.selection.top, data.selection.right, pt.y);
                    break;
                }
                case HTLEFT:
                {
                    Xy12Rect(&data.selection, pt.x, data.selection.top, data.selection.right, data.selection.bottom);
                    break;
                }
                case HTSIZE:
                {
                    auto xoff = pt.x - data.mouseDownPt.x;
                    auto yoff = pt.y - data.mouseDownPt.y;
                    data.selection.left += xoff;
                    data.selection.top += yoff;
                    data.selection.right += xoff;
                    data.selection.bottom += yoff;
                    data.mouseDownPt = pt;
                    break;
                }

                default:
                {
                    updateButtonPos = false;
                    break;
                }

                }

                if (updateButtonPos)
                {
                    const ScreenInfo& screen = native->screens->ScreenFromWorkspaceRect(data.selection);
                    SetButtonPanelPositions(screen, &data.selection, data.buttonPositions);
                }
            }
            else
            {
                // hit test
                FrameUpdateHitTest(data);
            }
        }
        else
        {
            if (data.mouseDown)
            {
                // user might be dragging a selection
                RECT psel = round_px_selection(data.mouseDownPt.x, data.mouseDownPt.y, data.mouse.x, data.mouse.y);
                auto dragDistance = UNSCALED_DRAG_THRESHOLD / (dpizoom * data.zoom);

                if (!data.dragging && (RECT_WIDTH(psel) > dragDistance || RECT_HEIGHT(psel) > dragDistance))
                {
                    data.dragging = true;
                }

                if (data.dragging)
                {
                    native->walker->ResetHitResult(&data.windowSelection);
                    data.selection = psel;
                }
            }
            else
            {
                // selection should come from the window currently underneath the cursor
                FrameUpdateHoveredWindow(data);
            }
        }

        SetFrame(&data);

        return 0;
    }

    case WM_MOUSEWHEEL:
    {
        mc_frame_data data{};
        GetFrame(&data);

        if (data.captured) return 0;

        auto zoom = data.zoom;
        auto zDelta = GET_WHEEL_DELTA_WPARAM(wParam);
        auto ctrlPressed = (LOWORD(wParam) & MK_CONTROL) > 0;
        auto shiftPressed = (LOWORD(wParam) & MK_SHIFT) > 0;
        if (ctrlPressed || shiftPressed)
        {
            if (zDelta > 0) zoom *= 1.05;
            else zoom /= 1.05;
        }
        else
        {
            if (zDelta > 0) zoom *= 2;
            else zoom /= 2;
        }

        zoom = min(max(zoom, 1), 256);

        if (zoom != data.zoom)
        {
            //if (zoom == 1) data.mouse = { floor(data.mouse.x), floor(data.mouse.y) };
            data.zoom = zoom;
            SetFrame(&data);
        }

        return 0;
    }

    }

    return DefWindowProc(hWnd, msg, wParam, lParam);
}

void DxScreenCapture::GetFrame(mc_frame_data* data)
{
    std::lock_guard<std::mutex> guard(sync);
    memcpy(data, &native->frame, sizeof(mc_frame_data));
}

void DxScreenCapture::SetFrame(mc_frame_data* data)
{
    if (data != nullptr)
    {
        std::lock_guard<std::mutex> guard(sync);

        auto cmp = memcmp(&native->frame, data, sizeof(mc_frame_data));
        if (cmp != 0)
        {
            memcpy(&native->frame, data, sizeof(mc_frame_data));

            for (int i = 0; i < windows.size(); i++)
            {
                render_info& wi = windows[i];
                SetEvent(wi.eventSignal);
            }
        }
    }
}

std::unique_ptr<NativeDib> DxScreenCapture::GetMergedBitmap(bool flipV, bool crop)
{
    mc_frame_data data{};
    GetFrame(&data);

    if (!data.captured) return nullptr;

    if (crop)
    {
        // produces a bitmap of only the user-cropped region, composed with a selected window on top (if exists)
        const RECT& sel = data.selection;
        int w = RECT_WIDTH(sel);
        int h = RECT_HEIGHT(sel);
        if (flipV) h *= -1; // <- note, height negative to flip the bitmap vertically

        unique_ptr<NativeDib> cropped = BitmapEx::Make24bppDib(w, h);
        if (data.windowSelection.window != nullptr && data.windowSelection.window->captured)
        {
            const RECT& rcWin = data.windowSelection.window->rcWorkspace;
            data.windowSelection.window->BitBltImage(cropped->GetBitmapDC(), rcWin.left - sel.left, rcWin.top - sel.top);
        }
        else
        {
            BitBlt(cropped->GetBitmapDC(), 0, 0, w, abs(h), native->screenshot->GetBitmapDC(), sel.left, sel.top, SRCCOPY);
        }

        return cropped;
    }
    else
    {
        // produces a bitmap of the entire desktop, composed with a selected window on top (if exists)
        auto sel = native->screens->WorkspaceBounds();
        int w = RECT_WIDTH(sel);
        int h = RECT_HEIGHT(sel);
        if (flipV) h *= -1; // <- note, height negative to flip the bitmap vertically

        unique_ptr<NativeDib> merged = BitmapEx::Make24bppDib(w, h);
        BitBlt(merged->GetBitmapDC(), 0, 0, w, abs(h), native->screenshot->GetBitmapDC(), 0, 0, SRCCOPY);

        if (data.windowSelection.window != nullptr && data.windowSelection.window->captured)
        {
            const RECT& rcWin = data.windowSelection.window->rcWorkspace;
            data.windowSelection.window->BitBltImage(merged->GetBitmapDC(), rcWin.left, rcWin.top);
        }

        return merged;
    }
}

System::Drawing::Color DxScreenCapture::GetFrameHoveredColor(mc_frame_data* data)
{
    int x = (int)floor(data->mouse.x);
    int y = (int)floor(data->mouse.y);

    int width = native->screenshot->GetWidth();
    int height = native->screenshot->GetHeight();

    x = max(0, min(x, width));
    y = max(0, min(y, height));

    uint32_t* pixels;
    native->screenshot->GetPixels((void**)&pixels);

    pixels += (y * width) + x;

    uint32_t px = *pixels;
    BYTE r = (px >> 16) & 0xFF;
    BYTE g = (px >> 8) & 0xFF;
    BYTE b = px & 0xFF;

    return Gdiplus::Color(r, g, b);
    //return System::Drawing::Color::FromArgb(r, g, b);
}

void DxScreenCapture::FrameUpdateHitTest(mc_frame_data& data)
{
    const ScreenInfo& screen = native->screens->ScreenFromWorkspacePt(data.mouse);
    double dpizoom = screen.dpi / BASE_DPI;
    POINT mpt = { (int)floor(data.mouse.x), (int)floor(data.mouse.y) };
    RECT& sel = data.selection;
    const int radius = (int)floor(UNSCALED_DRAG_HANDLE_SIZE * dpizoom);

    // hit test the capture buttons
    for (int i = 0; i < NUM_SVG_BUTTONS; i++)
    {
        if (PtInRect(&data.buttonPositions[i], mpt))
        {
            data.hittest = HTMENU;
            data.hittestBtn = i;
            return;
        }
    }

    // we were not hovering a button
    data.hittestBtn = -1;

    // hit test the selection rectangle / resize handles
    const int numHandles = 8;
    RECTHT handles[numHandles];
    handles[0].hit = HTTOPLEFT;
    handles[0].pt = PtToWidenedRect(radius, sel.left, sel.top);
    handles[1].hit = HTTOPRIGHT;
    handles[1].pt = PtToWidenedRect(radius, sel.right, sel.top);
    handles[2].hit = HTBOTTOMRIGHT;
    handles[2].pt = PtToWidenedRect(radius, sel.right, sel.bottom);
    handles[3].hit = HTBOTTOMLEFT;
    handles[3].pt = PtToWidenedRect(radius, sel.left, sel.bottom);
    handles[4].hit = HTTOP;
    handles[4].pt = LineToWidenedRect(radius, sel.left, sel.top, sel.right, sel.top);
    handles[5].hit = HTRIGHT;
    handles[5].pt = LineToWidenedRect(radius, sel.right, sel.top, sel.right, sel.bottom);
    handles[6].hit = HTBOTTOM;
    handles[6].pt = LineToWidenedRect(radius, sel.right, sel.bottom, sel.left, sel.bottom);
    handles[7].hit = HTLEFT;
    handles[7].pt = LineToWidenedRect(radius, sel.left, sel.top, sel.left, sel.bottom);

    int ht = 0;

    for (int i = 0; i < numHandles; i++)
    {
        const auto& item = handles[i];
        if (PtInRect(&item.pt, mpt))
        {
            ht = item.hit;
            break;
        }
    }

    if (ht == 0)
    {
        ht = PtInRect(&sel, mpt) ? HTSIZE : HTCLIENT;
    }

    data.hittest = ht;
}

void DxScreenCapture::FrameSetCursor(mc_frame_data& data)
{
    if (data.captured)
    {
        switch (data.hittest)
        {

        case HTSIZE:
        {
            SetCursor(LoadCursor(0, IDC_SIZEALL));
            break;
        }

        case HTTOP:
        case HTBOTTOM:
        {
            SetCursor(LoadCursor(0, IDC_SIZENS));
            break;
        }

        case HTLEFT:
        case HTRIGHT:
        {
            SetCursor(LoadCursor(0, IDC_SIZEWE));
            break;
        }

        case HTTOPRIGHT:
        case HTBOTTOMLEFT:
        {
            SetCursor(LoadCursor(0, IDC_SIZENESW));
            break;
        }

        case HTTOPLEFT:
        case HTBOTTOMRIGHT:
        {
            SetCursor(LoadCursor(0, IDC_SIZENWSE));
            break;
        }

        case HTMENU:
        {
            SetCursor(LoadCursor(0, IDC_HAND));
            break;
        }

        default:
        {
            SetCursor(LoadCursor(0, IDC_ARROW));
            break;
        }

        }
    }
    else
    {
        if (data.mouseAnchored)
        {
            SetCursor(0);
        }
        else
        {
            SetCursor(LoadCursor(0, IDC_ARROW));
        }
    }
}

void DxScreenCapture::FrameUpdateHoveredWindow(mc_frame_data& data)
{
    if (native->walker->HitTestV2(data.mouse, &data.windowSelection))
    {
        CopyRect(&data.selection, &data.windowSelection.idealSelection);
    }
    else
    {
        ResetRect(&data.selection);
    }
}

void DxScreenCapture::FrameMakeSelection(mc_frame_data& data, const RECT& sel, bool preserveWindow)
{
    data.dragging = false;
    data.mouseDown = false;
    data.zoom = 1;
    data.selection = sel;
    data.crosshair = true;

    bool rEmpty = IsRectEmpty(&sel);

    if (rEmpty && data.captured)
    {
        data.captured = false;
        if (!data.mouseAnchored)
        {
            data.mouseAnchored = true;
            native->screens->MouseAnchorStart(data.mouse);
        }
    }

    else if (!rEmpty && !data.captured)
    {
        data.captured = true;
        if (data.mouseAnchored)
        {
            data.mouseAnchored = false;
            native->screens->MouseAnchorStop(data.mouse);
        }
    }

    if (data.captured)
    {
        auto& wb = native->screens->WorkspaceBounds();
        ClipRectBy(&data.selection, &wb);
        FrameUpdateHitTest(data);
        if (!preserveWindow)
            native->walker->ResetHitResult(&data.windowSelection);

        // update "capture button" positions

        const ScreenInfo& screen = native->screens->ScreenFromWorkspaceRect(data.selection);
        SetButtonPanelPositions(screen, &data.selection, data.buttonPositions);
    }
    else
    {
        FrameUpdateHoveredWindow(data);
    }

    FrameSetCursor(data);
}

void DxScreenCapture::Reset()
{
    if (native != nullptr && native->disposed == 0 && primaryWindow)
        PostMessage(primaryWindow, WMEX_RESET, 0, 0);
}

void DxScreenCapture::Close(bool waitForExit)
{
    if (native != nullptr && native->disposed == 0 && primaryWindow)
        PostMessage(primaryWindow, WMEX_DESTROY, 0, 0);

    if (waitForExit)
        WaitForSingleObject(mainThread, 5000);
}

void DxScreenCapture::WriteToPointer(void* scan0, int dataSize)
{
    if (native == nullptr || native->disposed > 0)
        throw gcnew System::ObjectDisposedException("DxScreenCapture");

    void* pixels;
    auto cropped = GetMergedBitmap(true, true);
    if (cropped == nullptr) return;
    int desiredSize = cropped->GetSize();
    cropped->GetPixels(&pixels);

    if (dataSize < desiredSize)
        throw gcnew System::InvalidOperationException(string("Provided pointer does not have enough space. "
            + to_string(desiredSize) + " bytes needed, but only " + to_string(dataSize) + " provided.").c_str());

    memcpy(scan0, pixels, desiredSize);
}

void DxScreenCapture::WriteToClipboard()
{
    if (native == nullptr || native->disposed > 0)
        throw gcnew System::ObjectDisposedException("DxScreenCapture");

    DIBSECTION dib;
    HGLOBAL hGlobal;
    void* pixels;

    auto cropped = GetMergedBitmap(false, true);
    if (cropped == nullptr) return;

    cropped->GetDetails(&dib);
    cropped->GetPixels(&pixels);

    int pixelSize = dib.dsBmih.biSizeImage;
    int size = pixelSize + sizeof(BITMAPINFOHEADER);

    // allocate global memory
    hGlobal = GlobalAlloc(GMEM_ZEROINIT | GMEM_MOVEABLE, size);

    try
    {
        auto allocatedSize = GlobalSize(hGlobal);
        if (allocatedSize < size)
            throw gcnew System::OutOfMemoryException("Unable to allocate sufficient space for selected bitmap.");

        // copy bitmap data to global memory
        BYTE* ptr = (BYTE*)GlobalLock(hGlobal);
        memcpy(ptr, &dib.dsBmih, sizeof(BITMAPINFOHEADER));
        memcpy(ptr + sizeof(BITMAPINFOHEADER), pixels, pixelSize);
        GlobalUnlock(hGlobal);

        int retry = 10;
        BOOL success;
        while (true)
        {
            try
            {
                SetLastError(0);

                success = OpenClipboard(primaryWindow);
                if (!success) HR_last_error();

                success = EmptyClipboard();
                if (!success) HR_last_error();

                auto result = SetClipboardData(CF_DIB, hGlobal);
                if (result == NULL) HR_last_error();

                break;
            }
            catch (...)
            {
                if (retry--)
                {
                    Sleep(100);
                    continue;
                }
                else
                {
                    throw;
                }
            }
        }
    }
    catch (...)
    {
        // free global memory only on error, on success the system takes ownership.
        if (hGlobal != NULL) GlobalFree(hGlobal);
        throw;
    }
}

json rect2json(RECT& rc)
{
    json j;
    j["x"] = rc.left;
    j["y"] = rc.top;
    j["width"] = RECT_WIDTH(rc);
    j["height"] = RECT_HEIGHT(rc);
    return j;
}

System::String DxScreenCapture::SaveSession(System::String sessionDirectory, System::String createdUtc)
{
    if (!native->frame.captured)
        return nullptr;

    //if (!System::IO::Directory::Exists(sessionDirectory))
    //    throw gcnew System::InvalidOperationException("Session directory must exist");

    // converter - json lib only works with std::string not std::wstring
    using convert_type = std::codecvt_utf8<wchar_t>;
    std::wstring_convert<convert_type, wchar_t> converter;

    // write screenshot to file
    //auto sessionDir = msclr::interop::marshal_as<wstring>(sessionDirectory);
    auto& sessionDir = sessionDirectory;

    fs::path stdDir{ sessionDir };
    if (!fs::exists(stdDir))
        fs::create_directory(stdDir);

    auto ssPath = sessionDir + L"\\desktop.png";
    auto merged = GetMergedBitmap(true, false);
    merged->WriteToFilePNG(ssPath);

    // cropped image
    auto croppedPath = sessionDir + L"\\cropped.png";
    auto cropped = GetMergedBitmap(true, true);
    cropped->WriteToFilePNG(croppedPath);

    // write out window info
    auto wli = native->walker->List();
    auto wlilen = wli->size();
    json windows = json::array();
    for (int i = 0; i < wlilen; i++)
    {
        auto& wi = wli->at(i);

        if (&wi == native->frame.windowSelection.window)
        {
            json j;
            j["id"] = wi.index;
            j["selected"] = true;
            j["caption"] = converter.to_bytes(wi.caption);
            j["class"] = converter.to_bytes(wi.className);
            j["position"] = rect2json(wi.rcWindow);
            windows.push_back(j);
        }
        else if (wi.captured)
        {
            wstring windowName = L"window_" + to_wstring(i);
            int cX, cY;
            NativeDib* dib;
            wi.GetBitmap(&dib, &cX, &cY);
            auto wiPath = sessionDir + L"\\" + windowName + L".png";
            dib->WriteToFilePNG(wiPath);
            json j;
            j["id"] = wi.index;
            j["imgPath"] = converter.to_bytes(wiPath);
            j["caption"] = converter.to_bytes(wi.caption);
            j["class"] = converter.to_bytes(wi.className);
            j["position"] = rect2json(wi.rcWindow);
            windows.push_back(j);
        }
    }

    //auto date = msclr::interop::marshal_as<string>(System::DateTime::UtcNow.ToString("o"));

    // write session to file
    json root;
    root["createdUtc"] = converter.to_bytes(createdUtc);
    root["windows"] = windows;
    //root["rootPath"] = converter.to_bytes(sessionDir);
    root["desktopImgPath"] = converter.to_bytes(ssPath);
    root["previewImgPath"] = converter.to_bytes(croppedPath);
    root["croppedRect"] = rect2json(native->frame.selection);
    if (native->frame.windowSelection.window != nullptr)
        root["selectionWnd"] = native->frame.windowSelection.window->index;

    auto sp = sessionDir + L"\\session.json";
    std::ofstream o(sp);
    o << std::setw(4) << root << std::endl;

    return sp;
    //return msclr::interop::marshal_as<System::String^>(sp);
}

//RECT DxScreenCapture::GetSelection()
//{
//    return native->frame.selection;
//}
//
//bool DxScreenCapture::GetCaptured()
//{
//    return native->frame.sel
//}