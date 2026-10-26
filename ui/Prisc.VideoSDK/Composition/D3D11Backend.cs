using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Hand-rolled Direct3D 11 COM interop for <see cref="D3D11EncodeBridge"/>: a device on a
    /// given adapter, the 11.1/11.4 methods that open Direct3D 12 shared textures and fences
    /// (<c>OpenSharedResource1</c>, <c>OpenSharedFence</c>, <c>ID3D11DeviceContext4::Wait/Signal</c>),
    /// the video-processor API that converts BGRA render targets to NV12 on the GPU, and the
    /// staging-texture calls a self-check reads the result back with. Same style as
    /// <see cref="D3D12Backend"/>: raw vtable slots, no projection package. Every slot index and
    /// struct layout was checked against the Windows SDK 10.0.26100 <c>d3d11.h</c>,
    /// <c>d3d11_1.h</c>, <c>d3d11_3.h</c>, <c>d3d11_4.h</c> and <c>dxgi.h</c> vtable structs
    /// (none of these interfaces has a struct-returning method, so there are no hidden-return
    /// twins to account for).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe class D3D11Backend
    {
        // ID3D11Device: 3 CreateBuffer, 4 CreateTexture1D, 5 CreateTexture2D, ... 40 GetImmediateContext
        private const int VtblCreateTexture2D = 5;
        private const int VtblGetImmediateContext = 40;
        // ID3D11Device1 (d3d11_1.h): 43 GetImmediateContext1, ... 48 OpenSharedResource1
        private const int VtblOpenSharedResource1 = 48;
        // ID3D11Device5 (d3d11_4.h): 67 OpenSharedFence, 68 CreateFence
        private const int VtblOpenSharedFence = 67;
        // ID3D11DeviceContext: 14 Map, 15 Unmap, ... 46 CopySubresourceRegion, 47 CopyResource, ... 111 Flush
        private const int VtblContextMap = 14;
        private const int VtblContextUnmap = 15;
        private const int VtblContextCopyResource = 47;
        private const int VtblContextFlush = 111;
        // ID3D11DeviceContext4 (d3d11_3.h): 147 Signal, 148 Wait
        private const int VtblContext4Signal = 147;
        private const int VtblContext4Wait = 148;
        // ID3D11Multithread (d3d11_4.h): 3 Enter, 4 Leave, 5 SetMultithreadProtected, 6 GetMultithreadProtected
        private const int VtblSetMultithreadProtected = 5;
        // ID3D11VideoDevice: 3 CreateVideoDecoder, 4 CreateVideoProcessor, ... 8 CreateVideoProcessorInputView,
        // 9 CreateVideoProcessorOutputView, 10 CreateVideoProcessorEnumerator
        private const int VtblCreateVideoProcessor = 4;
        private const int VtblCreateVideoProcessorInputView = 8;
        private const int VtblCreateVideoProcessorOutputView = 9;
        private const int VtblCreateVideoProcessorEnumerator = 10;
        // ID3D11VideoProcessorEnumerator: 7 GetVideoProcessorContentDesc, 8 CheckVideoProcessorFormat
        private const int VtblCheckVideoProcessorFormat = 8;
        // ID3D11VideoContext: 7 GetDecoderBuffer .. 12 DecoderExtension, 13 VideoProcessorSetOutputTargetRect,
        // 14 ..BackgroundColor, 15 VideoProcessorSetOutputColorSpace, ... 27 VideoProcessorSetStreamFrameFormat,
        // 28 VideoProcessorSetStreamColorSpace, 29 ..OutputRate, 30 VideoProcessorSetStreamSourceRect,
        // 31 VideoProcessorSetStreamDestRect, ... 37 VideoProcessorSetStreamAutoProcessingMode, ... 53 VideoProcessorBlt
        private const int VtblSetOutputTargetRect = 13;
        private const int VtblSetOutputColorSpace = 15;
        private const int VtblSetStreamFrameFormat = 27;
        private const int VtblSetStreamColorSpace = 28;
        private const int VtblSetStreamSourceRect = 30;
        private const int VtblSetStreamDestRect = 31;
        private const int VtblSetStreamAutoProcessingMode = 37;
        private const int VtblVideoProcessorBlt = 53;
        // IDXGIDevice: 7 GetAdapter; IDXGIAdapter: 7 EnumOutputs, 8 GetDesc
        private const int VtblDxgiDeviceGetAdapter = 7;
        private const int VtblDxgiAdapterGetDesc = 8;

        // d3d11.h enum values used here
        public const int DriverTypeUnknown = 0;                   // D3D_DRIVER_TYPE_UNKNOWN (adapter given)
        public const uint CreateDeviceBgraSupport = 0x20;         // D3D11_CREATE_DEVICE_BGRA_SUPPORT
        public const uint CreateDeviceVideoSupport = 0x800;       // D3D11_CREATE_DEVICE_VIDEO_SUPPORT
        public const uint SdkVersion = 7;                         // D3D11_SDK_VERSION
        public const int FeatureLevel11_1 = 0xb100, FeatureLevel11_0 = 0xb000;
        public const int UsageDefault = 0;                        // D3D11_USAGE_DEFAULT
        public const int UsageStaging = 3;                        // D3D11_USAGE_STAGING
        public const uint BindShaderResource = 0x8;               // D3D11_BIND_SHADER_RESOURCE
        public const uint BindRenderTarget = 0x20;                // D3D11_BIND_RENDER_TARGET
        public const uint CpuAccessRead = 0x20000;                // D3D11_CPU_ACCESS_READ
        public const int MapRead = 1;                             // D3D11_MAP_READ
        public const int FormatB8G8R8A8Unorm = 87;                // DXGI_FORMAT_B8G8R8A8_UNORM
        public const int FormatNv12 = 103;                        // DXGI_FORMAT_NV12
        public const int VideoFrameFormatProgressive = 0;         // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
        public const int VideoUsageOptimalQuality = 2;            // D3D11_VIDEO_USAGE_OPTIMAL_QUALITY
        public const int VpivDimensionTexture2D = 1;              // D3D11_VPIV_DIMENSION_TEXTURE2D
        public const int VpovDimensionTexture2D = 1;              // D3D11_VPOV_DIMENSION_TEXTURE2D
        public const uint VideoProcessorFormatSupportInput = 0x1; // D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT
        public const uint VideoProcessorFormatSupportOutput = 0x2;// D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT

        // D3D11_VIDEO_PROCESSOR_COLOR_SPACE is a bitfield: Usage:1 RGB_Range:1 YCbCr_Matrix:1
        // YCbCr_xvYCC:1 Nominal_Range:2 Reserved:26 (LSB first).
        public const uint ColorSpaceUsageProcessing = 1u << 0;    // Usage = 1: video processing, not playback
        public const uint ColorSpaceRgbLimited = 1u << 1;         // RGB_Range = 1: 16-235 (0 = 0-255)
        public const uint ColorSpaceMatrixBt709 = 1u << 2;        // YCbCr_Matrix = 1: BT.709 (0 = BT.601)
        public const uint ColorSpaceNominalRange16To235 = 1u << 4;// Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235 (1)
        public const uint ColorSpaceNominalRange0To255 = 2u << 4; // Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255 (2)

        public static readonly Guid IID_ID3D11Device1 = new Guid("A04BFB29-08EF-43D6-A49C-A9BDBDCBE686");
        public static readonly Guid IID_ID3D11Device5 = new Guid("8FFDE202-A0E7-45DF-9E01-E837801B5EA0");
        public static readonly Guid IID_ID3D11DeviceContext4 = new Guid("917600DA-F58C-4C33-98D8-3E15B390FA24");
        public static readonly Guid IID_ID3D11Multithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        public static readonly Guid IID_ID3D11VideoDevice = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
        public static readonly Guid IID_ID3D11VideoContext = new Guid("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");
        public static readonly Guid IID_IDXGIDevice = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        private static readonly Guid IID_ID3D11Fence = new Guid("AFFDE9D1-1DF7-4BB7-8A34-0F46251DAB80");

        [DllImport("d3d11", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int* featureLevels, uint featureLevelCount, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* immediateContext);

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_TEXTURE2D_DESC // 44 bytes
        {
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint ArraySize;
            public int Format;
            public uint SampleCount;   // DXGI_SAMPLE_DESC.Count
            public uint SampleQuality; // DXGI_SAMPLE_DESC.Quality
            public int Usage;
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_MAPPED_SUBRESOURCE // 16 bytes
        {
            public void* pData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_VIDEO_PROCESSOR_CONTENT_DESC // 40 bytes
        {
            public int InputFrameFormat;      // D3D11_VIDEO_FRAME_FORMAT
            public uint InputFrameRateNum;    // DXGI_RATIONAL
            public uint InputFrameRateDen;
            public uint InputWidth;
            public uint InputHeight;
            public uint OutputFrameRateNum;   // DXGI_RATIONAL
            public uint OutputFrameRateDen;
            public uint OutputWidth;
            public uint OutputHeight;
            public int Usage;                 // D3D11_VIDEO_USAGE
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC // 16 bytes
        {
            public uint FourCC;        // 0: the resource's own format
            public int ViewDimension;  // D3D11_VPIV_DIMENSION
            public uint MipSlice;      // D3D11_TEX2D_VPIV
            public uint ArraySlice;
        }

        /// <summary>ViewDimension + a union of Texture2D {MipSlice} / Texture2DArray {MipSlice,
        /// FirstArraySlice, ArraySize}; the Texture2D form is the array form's prefix.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC // 16 bytes
        {
            public int ViewDimension;  // D3D11_VPOV_DIMENSION
            public uint MipSlice;
            public uint FirstArraySlice;
            public uint ArraySize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D11_VIDEO_PROCESSOR_STREAM // 72 bytes on x64 (five UINTs, padding, six pointers)
        {
            public int Enable; // BOOL
            public uint OutputIndex;
            public uint InputFrameOrField;
            public uint PastFrames;
            public uint FutureFrames;
            public IntPtr ppPastSurfaces;
            public IntPtr pInputSurface;
            public IntPtr ppFutureSurfaces;
            public IntPtr ppPastSurfacesRight;
            public IntPtr pInputSurfaceRight;
            public IntPtr ppFutureSurfacesRight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        /// <summary>DXGI_ADAPTER_DESC: DXGI_ADAPTER_DESC1 without the trailing Flags.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC
        {
            public fixed char Description[128];
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public nuint DedicatedVideoMemory;
            public nuint DedicatedSystemMemory;
            public nuint SharedSystemMemory;
            public long AdapterLuid;
        }

        /// <summary>D3D11CreateDevice on <paramref name="adapter"/> (driver type UNKNOWN, as the
        /// API requires when an adapter is given), feature level 11.1 or 11.0, with the given
        /// creation flags. The immediate context comes back AddRef'd.</summary>
        public static int CreateDevice(IntPtr adapter, uint flags, out IntPtr device, out IntPtr immediateContext, out int featureLevel)
        {
            int* levels = stackalloc int[2];
            levels[0] = FeatureLevel11_1;
            levels[1] = FeatureLevel11_0;
            IntPtr d, c;
            int level;
            int hr = D3D11CreateDevice(adapter, DriverTypeUnknown, IntPtr.Zero, flags, levels, 2, SdkVersion, &d, &level, &c);
            device = hr < 0 ? IntPtr.Zero : d;
            immediateContext = hr < 0 ? IntPtr.Zero : c;
            featureLevel = hr < 0 ? 0 : level;
            return hr;
        }

        // ------------------------------------------------------------------------ ID3D11Device

        /// <summary>ID3D11Device::CreateTexture2D with no initial data.</summary>
        public static int CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc, out IntPtr texture)
        {
            IntPtr t;
            int hr;
            fixed (D3D11_TEXTURE2D_DESC* d = &desc)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)(*(void***)device)[VtblCreateTexture2D];
                hr = fn(device, d, IntPtr.Zero, &t);
            }
            texture = hr < 0 ? IntPtr.Zero : t;
            return hr;
        }

        /// <summary>ID3D11Device::GetImmediateContext (AddRef'd).</summary>
        public static IntPtr GetImmediateContext(IntPtr device)
        {
            IntPtr c;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)(*(void***)device)[VtblGetImmediateContext];
            fn(device, &c);
            return c;
        }

        /// <summary>ID3D11Device1::OpenSharedResource1 of an NT handle as an ID3D11Texture2D.</summary>
        public static int OpenSharedTexture(IntPtr device1, IntPtr ntHandle, out IntPtr texture)
        {
            IntPtr t;
            int hr;
            fixed (Guid* riid = &IID_ID3D11Texture2D)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)device1)[VtblOpenSharedResource1];
                hr = fn(device1, ntHandle, riid, &t);
            }
            texture = hr < 0 ? IntPtr.Zero : t;
            return hr;
        }

        /// <summary>ID3D11Device5::OpenSharedFence of an NT handle as an ID3D11Fence.</summary>
        public static int OpenSharedFence(IntPtr device5, IntPtr ntHandle, out IntPtr fence)
        {
            IntPtr f;
            int hr;
            fixed (Guid* riid = &IID_ID3D11Fence)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)device5)[VtblOpenSharedFence];
                hr = fn(device5, ntHandle, riid, &f);
            }
            fence = hr < 0 ? IntPtr.Zero : f;
            return hr;
        }

        /// <summary>ID3D11Multithread::SetMultithreadProtected; returns the previous setting.</summary>
        public static bool SetMultithreadProtected(IntPtr multithread, bool enabled)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)(*(void***)multithread)[VtblSetMultithreadProtected];
            return fn(multithread, enabled ? 1 : 0) != 0;
        }

        // ----------------------------------------------------------------- ID3D11DeviceContext

        /// <summary>ID3D11DeviceContext::Flush — submits the queued work to the GPU.</summary>
        public static void ContextFlush(IntPtr context)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, void>)(*(void***)context)[VtblContextFlush];
            fn(context);
        }

        /// <summary>ID3D11DeviceContext::CopyResource (whole resource, same size and format).</summary>
        public static void ContextCopyResource(IntPtr context, IntPtr dst, IntPtr src)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)(*(void***)context)[VtblContextCopyResource];
            fn(context, dst, src);
        }

        /// <summary>ID3D11DeviceContext::Map of subresource 0 for reading (staging textures;
        /// blocks until the GPU has finished writing it).</summary>
        public static int ContextMapRead(IntPtr context, IntPtr resource, out D3D11_MAPPED_SUBRESOURCE mapped)
        {
            D3D11_MAPPED_SUBRESOURCE m;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)(*(void***)context)[VtblContextMap];
            int hr = fn(context, resource, 0, MapRead, 0, &m);
            mapped = hr < 0 ? default : m;
            return hr;
        }

        /// <summary>ID3D11DeviceContext::Unmap of subresource 0.</summary>
        public static void ContextUnmap(IntPtr context, IntPtr resource)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)(*(void***)context)[VtblContextUnmap];
            fn(context, resource, 0);
        }

        /// <summary>ID3D11DeviceContext4::Signal — the GPU sets the fence to <paramref name="value"/>
        /// once the work queued before this call has completed.</summary>
        public static int Context4Signal(IntPtr context4, IntPtr fence, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)(*(void***)context4)[VtblContext4Signal];
            return fn(context4, fence, value);
        }

        /// <summary>ID3D11DeviceContext4::Wait — the GPU stalls the work queued after this call
        /// until the fence reaches <paramref name="value"/> (a Direct3D 12 queue may be the signaller).</summary>
        public static int Context4Wait(IntPtr context4, IntPtr fence, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)(*(void***)context4)[VtblContext4Wait];
            return fn(context4, fence, value);
        }

        // ---------------------------------------------------------------------------- DXGI

        /// <summary>The LUID of the adapter a Direct3D 11 device runs on: IDXGIDevice::GetAdapter
        /// then IDXGIAdapter::GetDesc.</summary>
        public static int GetAdapterLuid(IntPtr device, out long luid)
        {
            luid = 0;
            int hr = D3D12Backend.QueryInterface(device, IID_IDXGIDevice, out var dxgiDevice);
            if (hr < 0)
                return hr;
            IntPtr adapter = IntPtr.Zero;
            try
            {
                var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)dxgiDevice)[VtblDxgiDeviceGetAdapter];
                IntPtr a;
                hr = getAdapter(dxgiDevice, &a);
                if (hr < 0)
                    return hr;
                adapter = a;
                DXGI_ADAPTER_DESC desc = default;
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)(*(void***)adapter)[VtblDxgiAdapterGetDesc];
                hr = getDesc(adapter, &desc);
                if (hr >= 0)
                    luid = desc.AdapterLuid;
                return hr;
            }
            finally
            {
                D3D12Backend.Release(adapter);
                D3D12Backend.Release(dxgiDevice);
            }
        }

        // ------------------------------------------------------------------- ID3D11VideoDevice

        /// <summary>ID3D11VideoDevice::CreateVideoProcessorEnumerator for the given content.</summary>
        public static int CreateVideoProcessorEnumerator(IntPtr videoDevice, in D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc, out IntPtr enumerator)
        {
            IntPtr e;
            int hr;
            fixed (D3D11_VIDEO_PROCESSOR_CONTENT_DESC* d = &desc)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_VIDEO_PROCESSOR_CONTENT_DESC*, IntPtr*, int>)(*(void***)videoDevice)[VtblCreateVideoProcessorEnumerator];
                hr = fn(videoDevice, d, &e);
            }
            enumerator = hr < 0 ? IntPtr.Zero : e;
            return hr;
        }

        /// <summary>ID3D11VideoProcessorEnumerator::CheckVideoProcessorFormat — the
        /// <c>D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_*</c> flags for a DXGI format.</summary>
        public static int CheckVideoProcessorFormat(IntPtr enumerator, int format, out uint supportFlags)
        {
            uint flags;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)(*(void***)enumerator)[VtblCheckVideoProcessorFormat];
            int hr = fn(enumerator, format, &flags);
            supportFlags = hr < 0 ? 0 : flags;
            return hr;
        }

        /// <summary>ID3D11VideoDevice::CreateVideoProcessor (rate conversion capability 0: no
        /// frame-rate conversion is asked of it).</summary>
        public static int CreateVideoProcessor(IntPtr videoDevice, IntPtr enumerator, out IntPtr processor)
        {
            IntPtr p;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)(*(void***)videoDevice)[VtblCreateVideoProcessor];
            int hr = fn(videoDevice, enumerator, 0, &p);
            processor = hr < 0 ? IntPtr.Zero : p;
            return hr;
        }

        /// <summary>ID3D11VideoDevice::CreateVideoProcessorInputView over mip 0 / slice 0 of a
        /// 2D texture in its own format.</summary>
        public static int CreateVideoProcessorInputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
        {
            var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = VpivDimensionTexture2D };
            IntPtr v;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC*, IntPtr*, int>)(*(void***)videoDevice)[VtblCreateVideoProcessorInputView];
            int hr = fn(videoDevice, texture, enumerator, &desc, &v);
            view = hr < 0 ? IntPtr.Zero : v;
            return hr;
        }

        /// <summary>ID3D11VideoDevice::CreateVideoProcessorOutputView over mip 0 of a 2D texture
        /// (which must carry <see cref="BindRenderTarget"/>).</summary>
        public static int CreateVideoProcessorOutputView(IntPtr videoDevice, IntPtr texture, IntPtr enumerator, out IntPtr view)
        {
            var desc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = VpovDimensionTexture2D };
            IntPtr v;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC*, IntPtr*, int>)(*(void***)videoDevice)[VtblCreateVideoProcessorOutputView];
            int hr = fn(videoDevice, texture, enumerator, &desc, &v);
            view = hr < 0 ? IntPtr.Zero : v;
            return hr;
        }

        // ------------------------------------------------------------------ ID3D11VideoContext

        /// <summary>ID3D11VideoContext::VideoProcessorSetOutputColorSpace (a
        /// <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> bitfield, see the <c>ColorSpace*</c> constants).</summary>
        public static void VideoProcessorSetOutputColorSpace(IntPtr videoContext, IntPtr processor, uint colorSpace)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, void>)(*(void***)videoContext)[VtblSetOutputColorSpace];
            fn(videoContext, processor, &colorSpace);
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetStreamColorSpace for one input stream.</summary>
        public static void VideoProcessorSetStreamColorSpace(IntPtr videoContext, IntPtr processor, uint stream, uint colorSpace)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, void>)(*(void***)videoContext)[VtblSetStreamColorSpace];
            fn(videoContext, processor, stream, &colorSpace);
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetStreamFrameFormat.</summary>
        public static void VideoProcessorSetStreamFrameFormat(IntPtr videoContext, IntPtr processor, uint stream, int frameFormat)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)(*(void***)videoContext)[VtblSetStreamFrameFormat];
            fn(videoContext, processor, stream, frameFormat);
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetStreamAutoProcessingMode — the driver's
        /// automatic "enhancements" (noise reduction, colour tweaks) on or off.</summary>
        public static void VideoProcessorSetStreamAutoProcessingMode(IntPtr videoContext, IntPtr processor, uint stream, bool enabled)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)(*(void***)videoContext)[VtblSetStreamAutoProcessingMode];
            fn(videoContext, processor, stream, enabled ? 1 : 0);
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetStreamSourceRect (enabled, the given rect).</summary>
        public static void VideoProcessorSetStreamSourceRect(IntPtr videoContext, IntPtr processor, uint stream, in RECT rect)
        {
            fixed (RECT* r = &rect)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, RECT*, void>)(*(void***)videoContext)[VtblSetStreamSourceRect];
                fn(videoContext, processor, stream, 1, r);
            }
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetStreamDestRect (enabled, the given rect).</summary>
        public static void VideoProcessorSetStreamDestRect(IntPtr videoContext, IntPtr processor, uint stream, in RECT rect)
        {
            fixed (RECT* r = &rect)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, RECT*, void>)(*(void***)videoContext)[VtblSetStreamDestRect];
                fn(videoContext, processor, stream, 1, r);
            }
        }

        /// <summary>ID3D11VideoContext::VideoProcessorSetOutputTargetRect (enabled, the given rect).</summary>
        public static void VideoProcessorSetOutputTargetRect(IntPtr videoContext, IntPtr processor, in RECT rect)
        {
            fixed (RECT* r = &rect)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, RECT*, void>)(*(void***)videoContext)[VtblSetOutputTargetRect];
                fn(videoContext, processor, 1, r);
            }
        }

        /// <summary>ID3D11VideoContext::VideoProcessorBlt of one enabled progressive stream from
        /// <paramref name="inputView"/> into <paramref name="outputView"/> (output frame 0).</summary>
        public static int VideoProcessorBlt(IntPtr videoContext, IntPtr processor, IntPtr outputView, IntPtr inputView)
        {
            var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = 1, pInputSurface = inputView };
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, D3D11_VIDEO_PROCESSOR_STREAM*, int>)(*(void***)videoContext)[VtblVideoProcessorBlt];
            return fn(videoContext, processor, outputView, 0, 1, &stream);
        }
    }
}
