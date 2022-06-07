#pragma once
#include "pch.h"

#include <dxgi1_3.h>
#include <d3d11_3.h>
#include <d2d1_3.h>
#include <d2d1effects_2.h>
#include <dwrite_3.h>
#include "NativeBitmap.h"

#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dxguid.lib")
#pragma comment(lib, "dwrite.lib")

inline D2D1_RECT_F Rect2D2D1(const RECT* r)
{
    return D2D1::RectF(r->left, r->top, r->right, r->bottom);
}

struct DxDisplay : public DXGI_OUTPUT_DESC
{
    int AdapterIdx;
    int OutputIdx;
    WCHAR AdapterDescription[128];
    DxDisplay(int adp, int output, DXGI_ADAPTER_DESC& adaptDesc, DXGI_OUTPUT_DESC& outputDesc)
    {
        AdapterIdx = adp;
        OutputIdx = output;
        std::wcsncpy(DeviceName, outputDesc.DeviceName, 32);
        std::wcsncpy(AdapterDescription, adaptDesc.Description, 128);
        DesktopCoordinates = outputDesc.DesktopCoordinates;
        AttachedToDesktop = outputDesc.AttachedToDesktop;
        Rotation = outputDesc.Rotation;
        Monitor = outputDesc.Monitor;
    }
};

struct DxPerfStats
{
    std::deque<double> times;
    double frameAvg;
    double frameMax;
    double frameMin;
    double frameStdDev;

    DxPerfStats() : frameAvg(0), frameMax(0), frameMin(0), frameStdDev(0) { }

    void addTime(std::chrono::steady_clock::time_point startTime, std::chrono::steady_clock::time_point endTime)
    {
        int capacity = min(400, max(60, frameAvg > 0 ? (1000 / frameAvg) : 0));
        int remove = max(0, times.size() - capacity);

        if (remove > 0)
            times.erase(times.begin(), times.begin() + remove);

        auto frameTimeNs = endTime - startTime;
        double frameTimeMs = frameTimeNs.count() / (double)NS_TO_MS_DIV;

        times.push_back(frameTimeMs);

        double fmax = frameTimeMs, fmin = frameTimeMs;

        double sum = std::accumulate(std::begin(times), std::end(times), 0.0);
        double m = sum / times.size();

        double accum = 0.0;
        std::for_each(std::begin(times), std::end(times),
            [&](const double d)
            {
                fmax = max(fmax, d);
                fmin = min(fmin, d);
                accum += (d - m) * (d - m);
            }
        );

        double stdev = sqrt(accum / (times.size() - 1));

        frameAvg = m;
        frameMax = fmax;
        frameMin = fmin;
        frameStdDev = stdev;
    }

    std::wstring detail()
    {
        return L"avg: " + to_wfmt(frameAvg)
            + L",  min: " + to_wfmt(frameMin)
            + L",  max: " + to_wfmt(frameMax)
            + L"  //  sd: " + to_wfmt(frameStdDev)
            + L"  (" + std::to_wstring(times.size()) + L")";
    }
};

class DxOutputDevice
{

private:
    // devices
    IDXGIFactory2* dxgi;
    ID2D1Factory6* d2d1;
    IDWriteFactory* dwrite;
    IDXGIAdapter1* adapter;
    ID3D11Device* devd3d;
    IDXGIDevice* devdxgi;
    ID2D1Device5* devd2d1;
    IDXGISwapChain2* swap2;
    HANDLE waitobj;
    ID2D1DeviceContext5* context;
    IDXGISurface2* surface;
    ID2D1Bitmap1* bitmap;

    // performance statistics
    DXGI_FRAME_STATISTICS stats;
    HRESULT statshr;
    int frames_dropped;
    DxPerfStats frames_overall;
    DxPerfStats frames_draw;
    DxPerfStats frames_wait;
    DxPerfStats frames_present;
    std::chrono::steady_clock::time_point f0;
    std::chrono::steady_clock::time_point f1;
    std::chrono::steady_clock::time_point f2;
    std::chrono::steady_clock::time_point f3;

    // other
    bool drawing;
    void Destroy();

public:
    DxOutputDevice(HWND hwnd, DxDisplay display);
    ~DxOutputDevice();

    static void EnumOutputs(std::vector<DxDisplay>& monitors);
    void BeginDraw(ID2D1DeviceContext5** dc);
    void EndDraw();

    std::wstring GetDebugText()
    {
        return
            L"fps: " + to_wfmt(floor(1000.0 / frames_overall.frameAvg), L"%04.0f") + L",  dropped: " + std::to_wstring(frames_dropped)
            + L"\n  ....wait " + frames_wait.detail()
            + L"\n  ....draw " + frames_draw.detail()
            + L"\n  .present " + frames_present.detail()
            + L"\n  .overall " + frames_overall.detail();
    }

    void CreateFontFormat(DxRef<IDWriteTextFormat>& format, FLOAT fontSizePt, DWRITE_FONT_WEIGHT weight, bool monospaced);
    void CreateFontLayout(DxRef<IDWriteTextLayout>& layout, IDWriteTextFormat* format, const std::wstring& txt, DWRITE_TEXT_METRICS* metrics, FLOAT maxW, FLOAT maxH);

    void CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const D2D1_COLOR_F& color);
    void CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const FLOAT r, const FLOAT g, const FLOAT b, const FLOAT a = 1.0f);
    void CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, System::Drawing::Color color);
    void CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, System::Drawing::Color color, FLOAT a);
    void CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const D2D1::ColorF::Enum color, FLOAT a = 1.0f);
    void CreateBitmapFrom32bppGdiDib(DxRef<ID2D1Bitmap>& bitmap, NativeDib* dib);
    void CreateSvgDocumentFromResource(DxRef<ID2D1SvgDocument>& document, const D2D1_SIZE_F& size, LPCWSTR lpName, LPCWSTR lpType);
    void CreateEffect(DxRef<ID2D1Effect>& effect, const IID& effid, ID2D1Image* input);
    void CreateEffect(DxRef<ID2D1Effect>& effect, const IID& effid);
    void CreateStrokeStyle(DxRef<ID2D1StrokeStyle>& dash, FLOAT dashLength, FLOAT dashOffset);

    void PushOpacityLayer(DxRef<ID2D1Layer>& layer, FLOAT opacity);
};
