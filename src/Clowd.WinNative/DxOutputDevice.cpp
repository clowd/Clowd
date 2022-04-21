#include "pch.h"
#include "DxOutputDevice.h"

void DxOutputDevice::EnumOutputs(std::vector<DxDisplay>& monitors)
{
    DxRef<IDXGIFactory2> dxgi;
    HR(CreateDXGIFactory2(0, dxgi.uid(), dxgi.put_void()));

    for (int i = 0; i < 20; i++)
    {
        DxRef<IDXGIAdapter1> adp;
        if (FAILED(dxgi->EnumAdapters1(i, adp.put())))
            break; // we have finished searching

        DXGI_ADAPTER_DESC adpdesc;
        if (SUCCEEDED(adp->GetDesc(&adpdesc)))
        {
            for (int z = 0; z < 20; z++)
            {
                DxRef<IDXGIOutput> outp;
                if (FAILED(adp->EnumOutputs(z, outp.put())))
                    break; // we've finished searching 

                DXGI_OUTPUT_DESC desc;
                if (SUCCEEDED(outp->GetDesc(&desc)) && desc.AttachedToDesktop)
                    monitors.emplace_back(i, z, adpdesc, desc);
            }
        }
    }
}

DxOutputDevice::DxOutputDevice(HWND hwnd, DxDisplay display)
{
    drawing = false;
    statshr = -1;
    frames_dropped = 0;

    dxgi = 0;
    d2d1 = 0;
    dwrite = 0;
    adapter = 0;
    devd3d = 0;
    devdxgi = 0;
    devd2d1 = 0;
    swap2 = 0;
    waitobj = 0;
    context = 0;
    surface = 0;
    bitmap = 0;

    try
    {
        // dxgi factory
        UINT dxflag = 0;
#if _DEBUG
        dxflag |= DXGI_CREATE_FACTORY_DEBUG;
#endif
        HR(CreateDXGIFactory2(dxflag, __uuidof(dxgi), reinterpret_cast<void**>(&dxgi)));

        D2D1_FACTORY_OPTIONS const options = {
    #if _DEBUG
            D2D1_DEBUG_LEVEL_INFORMATION
    #else
            D2D1_DEBUG_LEVEL_ERROR
    #endif
        };

        // d2d1 factory
        HR(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, options, &d2d1));

        // dwrite factory
        HR(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(&dwrite)));

        int width = display.DesktopCoordinates.right - display.DesktopCoordinates.left;
        int height = display.DesktopCoordinates.bottom - display.DesktopCoordinates.top;

        HR(dxgi->EnumAdapters1(display.AdapterIdx, &adapter));

        // create d3d11 device on the correct display adapter. 
        UINT d3dFlags
            = D3D11_CREATE_DEVICE_BGRA_SUPPORT;		// enable interoperability with D2D.
            //| D3D11_CREATE_DEVICE_SINGLETHREADED;	// disable thread-synchronization/locking for higher performance

#if _DEBUG
        d3dFlags |= D3D11_CREATE_DEVICE_DEBUG;		// enhanced debugging information
#endif

        HR(D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN, // must be unknown when using specific adapter
            nullptr,    // module for software rasterizer - n/a
            d3dFlags,
            nullptr, 0, // use the highest available d3d feature level
            D3D11_SDK_VERSION,
            &devd3d,
            nullptr,    // actual feature level (don't care) ... (probably should...)
            nullptr));

        // get dxgi interface for d3d device
        HR(devd3d->QueryInterface(&devdxgi));

        // create d2d1 device which interops with d3d device using dxgi interface
        HR(d2d1->CreateDevice(devdxgi, &devd2d1));

        // create d2d1 drawing device context with d2d1 device
        HR(devd2d1->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context));

        // create swap chain
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        description.BufferCount = 2;
        description.SampleDesc.Count = 1; // only 1 Anti-Alias sample (more here is higher quality - 4 is maximum safe value)
        description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        description.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT; // requires DX 11.2
        description.Width = width;
        description.Height = height;

        // create swap chain. our D2D1 bitmaps will render to this swap chain. 
        // note: CreateSwapChainForComposition can be used to create a fully transparent window
        DxRef<IDXGISwapChain1> swap1;
        HR(dxgi->CreateSwapChainForHwnd(devdxgi, hwnd, &description, nullptr, nullptr, swap1.put()));
        HR(swap1->QueryInterface(&swap2));
        HR(swap2->SetMaximumFrameLatency(1));

        waitobj = swap2->GetFrameLatencyWaitableObject();
    }
    catch (...)
    {
        // since deconstructor will not be called if there is an error in the constructor
        Destroy();
        throw;
    }

}

DxOutputDevice::~DxOutputDevice()
{
    Destroy();
}

void DxOutputDevice::Destroy()
{
    SafeRelease(&bitmap);
    SafeRelease(&surface);
    SafeRelease(&context);
    CloseHandle(waitobj);
    SafeRelease(&swap2);
    SafeRelease(&devd2d1);
    SafeRelease(&devdxgi);
    SafeRelease(&devd3d);
    SafeRelease(&adapter);
    SafeRelease(&dwrite);
    SafeRelease(&d2d1);
    SafeRelease(&dxgi);
}

void DxOutputDevice::BeginDraw(ID2D1DeviceContext5** dc)
{
    if (drawing)
        throw gcnew System::InvalidOperationException("Cannot call BeginDraw twice in a row. Please call EndDraw before calling BeginDraw again.");

    drawing = true;
    f0 = std::chrono::high_resolution_clock::now();

    // get a reference to current back buffer
    HR(swap2->GetBuffer(0, __uuidof(surface), reinterpret_cast<void**>(&surface)));

    // Create a Direct2D bitmap that points to the swap chain surface
    D2D1_BITMAP_PROPERTIES1 properties = {};
    properties.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;
    properties.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    properties.bitmapOptions = D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;

    HR(context->CreateBitmapFromDxgiSurface(surface, properties, &bitmap));

    // Point the device context to the bitmap for rendering
    context->SetTarget(bitmap);

    // wait until last v-sync before drawing. this reduces paint-to-screen latency significantly
    WaitForSingleObjectEx(waitobj, 1000, true);

    f1 = std::chrono::high_resolution_clock::now();

    // Begin drawing
    context->BeginDraw();
    context->Clear();

    *dc = context;
}

void DxOutputDevice::EndDraw()
{
    if (!drawing) return;
    drawing = false;

    HR(context->EndDraw());

    f2 = std::chrono::high_resolution_clock::now();

    HR(swap2->Present(1, 0));

    // TODO, handle following responses from Present
    // DXGI_STATUS_OCCLUDED
    // DXGI_ERROR_DEVICE_REMOVED
    // DXGI_ERROR_DEVICE_RESET

    f3 = std::chrono::high_resolution_clock::now();

    SafeRelease(&bitmap);
    SafeRelease(&surface);

    // record frame times
    frames_overall.addTime(f0, f3);
    frames_draw.addTime(f1, f2);
    frames_wait.addTime(f0, f1);
    frames_present.addTime(f2, f3);

    // record dropped frames
    DXGI_FRAME_STATISTICS cs;
    HRESULT cshr = swap2->GetFrameStatistics(&cs);

    if (SUCCEEDED(cshr) && SUCCEEDED(statshr))
    {
        if (cs.PresentRefreshCount > 0 && stats.PresentRefreshCount > 0)
        {
            int vsyncSinceLastPresent = cs.PresentRefreshCount - stats.PresentRefreshCount;
            if (vsyncSinceLastPresent > 1 && vsyncSinceLastPresent < 100) // we skipped 1 or more frames
            {
                frames_dropped += (vsyncSinceLastPresent - 1);
            }
        }
    }

    statshr = cshr;
    stats = cs;
}

void DxOutputDevice::CreateFontFormat(DxRef<IDWriteTextFormat>& format, FLOAT fontSizePt, DWRITE_FONT_WEIGHT weight, bool monospaced)
{
    auto family = monospaced ? L"Consolas" : L"Segoe UI";
    HR(dwrite->CreateTextFormat(family, NULL, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, fontSizePt, L"en-us", format.put()));
}

void DxOutputDevice::CreateFontLayout(DxRef<IDWriteTextLayout>& layout, IDWriteTextFormat* format, std::wstring txt, DWRITE_TEXT_METRICS* metrics, FLOAT maxW, FLOAT maxH)
{
    HR(dwrite->CreateTextLayout(txt.c_str(), txt.size(), format, maxW, maxH, layout.put()));
    if (metrics != nullptr) HR((layout)->GetMetrics(metrics));
}

void DxOutputDevice::CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const D2D1_COLOR_F& color)
{
    HR(context->CreateSolidColorBrush(color, brush.put()));
}

void DxOutputDevice::CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const FLOAT r, const FLOAT g, const FLOAT b, const FLOAT a)
{
    return CreateSolidBrush(brush, D2D1::ColorF(r, g, b, a));
}

void DxOutputDevice::CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, System::Drawing::Color color)
{
    return CreateSolidBrush(brush, D2D1::ColorF(color.GetR() / 255.0f, color.GetG() / 255.0f, color.GetB() / 255.0f, color.GetA() / 255.0f));
}

void DxOutputDevice::CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, System::Drawing::Color color, FLOAT a)
{
    return CreateSolidBrush(brush, D2D1::ColorF(color.GetR() / 255.0f, color.GetG() / 255.0f, color.GetB() / 255.0f, a));
}

void DxOutputDevice::CreateSolidBrush(DxRef<ID2D1SolidColorBrush>& brush, const D2D1::ColorF::Enum color, FLOAT a)
{
    return CreateSolidBrush(brush, D2D1::ColorF(color, a));
}

void DxOutputDevice::CreateBitmapFrom32bppGdiDib(DxRef<ID2D1Bitmap>& bitmap, NativeDib* dib)
{
    D2D1_BITMAP_PROPERTIES properties{};
    properties.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    properties.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;

    void* pixels;
    dib->GetPixels(&pixels);

    D2D1_SIZE_U d2size;
    d2size.width = dib->GetWidth();
    d2size.height = dib->GetHeight();

    HR(context->CreateBitmap(d2size, pixels, d2size.width * 4, &properties, bitmap.put()));
}

// https://stackoverflow.com/a/557774/184746
HMODULE GetCurrentModule()
{
    HMODULE hModule = NULL;
    GetModuleHandleEx(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        (LPCTSTR)GetCurrentModule,
        &hModule);

    return hModule;
}

BYTE* GetByteResource(LPCWSTR lpName, LPCWSTR lpType, DWORD* cBuf)
{
    HMODULE mod = GetCurrentModule();
    if (!mod) HR_last_error();

    auto f = FindResource(mod, lpName, lpType);
    if (!f) HR_last_error();

    auto r = LoadResource(mod, f);
    if (!f) HR_last_error();

    *cBuf = SizeofResource(mod, f);
    return (BYTE*)LockResource(r);
}

void DxOutputDevice::CreateSvgDocumentFromResource(DxRef<ID2D1SvgDocument>& document, const D2D1_SIZE_F& size, LPCWSTR lpName, LPCWSTR lpType)
{
    // find resource
    DWORD cRes;
    BYTE* pRes = GetByteResource(lpName, lpType, &cRes);

    // copy resource to global memory
    HGLOBAL hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, cRes);
    void* pGlobal = GlobalLock(hGlobal);
    memcpy(pGlobal, pRes, cRes);
    GlobalUnlock(hGlobal);

    // create svg doc
    DxRef<IStream> svgStream;
    try
    {
        HR(CreateStreamOnHGlobal(hGlobal, TRUE, svgStream.put()));
        context->CreateSvgDocument(svgStream, size, document.put());
    }
    catch (...)
    {
        // if createstream succeeds, hglobal will be cleaned up automatically
        GlobalFree(hGlobal);
        throw;
    }
}

void DxOutputDevice::CreateEffect(DxRef<ID2D1Effect>& effect, const IID& effid, ID2D1Image* input)
{
    // https://docs.microsoft.com/en-us/windows/win32/direct2d/built-in-effects
    HR(context->CreateEffect(effid, effect.put()));
    effect->SetInput(0, input);
}

void DxOutputDevice::CreateEffect(DxRef<ID2D1Effect>& effect, const IID& effid)
{
    // https://docs.microsoft.com/en-us/windows/win32/direct2d/built-in-effects
    HR(context->CreateEffect(effid, effect.put()));
}

void DxOutputDevice::CreateStrokeStyle(DxRef<ID2D1StrokeStyle>& dash, FLOAT dashLength, FLOAT dashOffset)
{
    FLOAT dashes[] = { dashLength, dashLength };

    auto properties = D2D1::StrokeStyleProperties(
        D2D1_CAP_STYLE_FLAT,
        D2D1_CAP_STYLE_FLAT,
        D2D1_CAP_STYLE_FLAT, // make this round?
        D2D1_LINE_JOIN_MITER,
        10.0f,
        D2D1_DASH_STYLE_CUSTOM,
        dashOffset
    );

    HR(d2d1->CreateStrokeStyle(properties, dashes, ARRAYSIZE(dashes), dash.put()));
}

void DxOutputDevice::PushOpacityLayer(DxRef<ID2D1Layer>& layer, FLOAT opacity)
{
    HR(context->CreateLayer(layer.put()));
    context->PushLayer(D2D1::LayerParameters(
        D2D1::InfiniteRect(),
        NULL,
        D2D1_ANTIALIAS_MODE_ALIASED,
        D2D1::IdentityMatrix(),
        opacity,
        NULL,
        D2D1_LAYER_OPTIONS_NONE), layer);
}