using System;
using System.Runtime.InteropServices;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Minimal hand-rolled D3D12 COM interop for the headless Skia GPU backend: device creation
    /// (factory → hardware adapter → device → direct command queue) to fill a
    /// <c>GRD3DBackendContext</c>, plus the handful of device/queue/fence/resource/command-list
    /// methods <see cref="D3D12ReadbackRing"/> needs to copy Skia's render targets into readback
    /// heaps on a copy queue, and the shared-handle / adapter-LUID calls
    /// <see cref="D3D11EncodeBridge"/> uses to hand those render targets to a Direct3D 11 device
    /// (see <see cref="D3D11Backend"/>). Deliberately no Windows SDK projection / TerraFX dependency: a
    /// couple of dozen calls do not justify a package. COM methods are invoked through raw
    /// vtable slots; every slot index below was checked against the Windows SDK 10.0.26100
    /// <c>d3d12.h</c> / <c>dxgi.h</c> vtable structs.
    /// </summary>
    internal static unsafe class D3D12Backend
    {
        // IUnknown:                     0 QueryInterface, 1 AddRef, 2 Release
        // IDXGIObject:                  3..6
        // IDXGIFactory:                 7 EnumAdapters .. 11 CreateSoftwareAdapter
        // IDXGIFactory1:                12 EnumAdapters1, 13 IsCurrent
        private const int VtblQueryInterface = 0;
        private const int VtblAddRef = 1;
        private const int VtblRelease = 2;
        private const int VtblEnumAdapters1 = 12;

        // IDXGIAdapter: 7 EnumOutputs, 8 GetDesc, 9 CheckInterfaceSupport; IDXGIAdapter1: 10 GetDesc1
        private const int VtblGetDesc1 = 10;

        // ID3D12Object: 3..6; ID3D12Device: 7 GetNodeCount, 8 CreateCommandQueue,
        // 9 CreateCommandAllocator, 10 CreateGraphicsPipelineState, 11 CreateComputePipelineState,
        // 12 CreateCommandList, ... 27 CreateCommittedResource, ... 31 CreateSharedHandle,
        // 32 OpenSharedHandle, ... 36 CreateFence, 37 GetDeviceRemovedReason,
        // 38 GetCopyableFootprints, ... 43 GetAdapterLuid (the two struct-returning methods
        // before it, GetResourceAllocationInfo and GetCustomHeapProperties, occupy one slot each
        // on Windows despite the header's #if !defined(_WIN32) twin declarations)
        private const int VtblCreateCommandQueue = 8;
        private const int VtblCreateCommandAllocator = 9;
        private const int VtblCreateCommandList = 12;
        private const int VtblCreateCommittedResource = 27;
        private const int VtblCreateSharedHandle = 31;
        private const int VtblCreateFence = 36;
        private const int VtblGetCopyableFootprints = 38;
        private const int VtblGetAdapterLuid = 43;

        // ID3D12CommandQueue (ID3D12Pageable: 3..7): 8 UpdateTileMappings, 9 CopyTileMappings,
        // 10 ExecuteCommandLists, 11 SetMarker, 12 BeginEvent, 13 EndEvent, 14 Signal, 15 Wait
        private const int VtblExecuteCommandLists = 10;
        private const int VtblQueueSignal = 14;
        private const int VtblQueueWait = 15;

        // ID3D12Fence: 8 GetCompletedValue, 9 SetEventOnCompletion, 10 Signal
        private const int VtblFenceGetCompletedValue = 8;
        private const int VtblFenceSetEventOnCompletion = 9;
        private const int VtblFenceSignal = 10;

        // ID3D12Resource: 8 Map, 9 Unmap, 10 GetDesc, 11 GetGPUVirtualAddress
        private const int VtblResourceMap = 8;
        private const int VtblResourceUnmap = 9;

        // ID3D12GraphicsCommandList (ID3D12CommandList: 8 GetType): 9 Close, 10 Reset,
        // 11 ClearState, 12 DrawInstanced, 13 DrawIndexedInstanced, 14 Dispatch,
        // 15 CopyBufferRegion, 16 CopyTextureRegion
        private const int VtblCommandListClose = 9;
        private const int VtblCopyTextureRegion = 16;

        private const uint DxgiErrorNotFound = 0x887A0002;
        private const uint DxgiAdapterFlagSoftware = 2;
        private const int FeatureLevel11_0 = 0xb000;
        private const uint GenericAll = 0x10000000; // GENERIC_ALL: the access a shared handle grants

        // D3D12 enum values used below (d3d12.h)
        public const int CommandListTypeDirect = 0;              // D3D12_COMMAND_LIST_TYPE_DIRECT
        public const int CommandListTypeCopy = 3;                // D3D12_COMMAND_LIST_TYPE_COPY
        public const int HeapTypeDefault = 1;                    // D3D12_HEAP_TYPE_DEFAULT
        public const int HeapTypeReadback = 3;                   // D3D12_HEAP_TYPE_READBACK
        public const int HeapFlagNone = 0;                       // D3D12_HEAP_FLAG_NONE
        public const int HeapFlagShared = 0x1;                   // D3D12_HEAP_FLAG_SHARED
        public const int ResourceDimensionBuffer = 1;            // D3D12_RESOURCE_DIMENSION_BUFFER
        public const int ResourceDimensionTexture2D = 3;         // D3D12_RESOURCE_DIMENSION_TEXTURE2D
        public const int TextureLayoutUnknown = 0;               // D3D12_TEXTURE_LAYOUT_UNKNOWN
        public const int TextureLayoutRowMajor = 1;              // D3D12_TEXTURE_LAYOUT_ROW_MAJOR
        public const int ResourceFlagAllowRenderTarget = 0x1;    // D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET
        public const int ResourceFlagAllowSimultaneousAccess = 0x20; // D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS
        public const int ResourceStateCommon = 0;                // D3D12_RESOURCE_STATE_COMMON
        public const int ResourceStateCopyDest = 0x400;          // D3D12_RESOURCE_STATE_COPY_DEST
        public const int FenceFlagNone = 0;                      // D3D12_FENCE_FLAG_NONE
        public const int FenceFlagShared = 0x1;                  // D3D12_FENCE_FLAG_SHARED
        public const int TextureCopyTypeSubresourceIndex = 0;    // D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX
        public const int TextureCopyTypePlacedFootprint = 1;     // D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT
        public const int FormatB8G8R8A8Unorm = 87;               // DXGI_FORMAT_B8G8R8A8_UNORM

        private static readonly Guid IID_ID3D12Device = new Guid("189819F1-1DB6-4B57-BE54-1821339B85F7");
        private static readonly Guid IID_IDXGIFactory1 = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        private static readonly Guid IID_ID3D12CommandQueue = new Guid("0EC870A6-5D7E-4C22-8CFC-5BAAE07616ED");
        private static readonly Guid IID_ID3D12Resource = new Guid("696442BE-A72E-4059-BC79-5B5C98040FAD");
        private static readonly Guid IID_ID3D12Fence = new Guid("0A753DCF-C4D8-4B91-ADF6-BE5A60D95A76");
        private static readonly Guid IID_ID3D12CommandAllocator = new Guid("6102DEE4-AF59-4B09-B999-B44D73F09B24");
        private static readonly Guid IID_ID3D12GraphicsCommandList = new Guid("5B160D0F-AC1B-4185-8BA8-B3AE42A5A455");

        [DllImport("dxgi", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* ppFactory);

        [DllImport("d3d12", ExactSpelling = true)]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, Guid* riid, IntPtr* ppDevice);

        [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
        private static extern int CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
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
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D12_COMMAND_QUEUE_DESC
        {
            public int Type;     // D3D12_COMMAND_LIST_TYPE_*
            public int Priority; // D3D12_COMMAND_QUEUE_PRIORITY_NORMAL = 0
            public int Flags;    // D3D12_COMMAND_QUEUE_FLAG_NONE = 0
            public uint NodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_HEAP_PROPERTIES // 20 bytes
        {
            public int Type;                 // D3D12_HEAP_TYPE_*
            public int CPUPageProperty;      // D3D12_CPU_PAGE_PROPERTY_UNKNOWN = 0
            public int MemoryPoolPreference; // D3D12_MEMORY_POOL_UNKNOWN = 0
            public uint CreationNodeMask;
            public uint VisibleNodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_RESOURCE_DESC // 56 bytes (8-aligned: Alignment at offset 8)
        {
            public int Dimension;
            public ulong Alignment;
            public ulong Width;
            public uint Height;
            public ushort DepthOrArraySize;
            public ushort MipLevels;
            public int Format;
            public uint SampleCount;   // DXGI_SAMPLE_DESC.Count
            public uint SampleQuality; // DXGI_SAMPLE_DESC.Quality
            public int Layout;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_SUBRESOURCE_FOOTPRINT // 20 bytes
        {
            public int Format;
            public uint Width;
            public uint Height;
            public uint Depth;
            public uint RowPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_PLACED_SUBRESOURCE_FOOTPRINT // 32 bytes (8-aligned)
        {
            public ulong Offset;
            public D3D12_SUBRESOURCE_FOOTPRINT Footprint;
        }

        /// <summary>pResource + Type + a union of PlacedFootprint / SubresourceIndex at offset 16.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        public struct D3D12_TEXTURE_COPY_LOCATION
        {
            [FieldOffset(0)] public IntPtr pResource;
            [FieldOffset(8)] public int Type; // D3D12_TEXTURE_COPY_TYPE_*
            [FieldOffset(16)] public D3D12_PLACED_SUBRESOURCE_FOOTPRINT PlacedFootprint;
            [FieldOffset(16)] public uint SubresourceIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3D12_RANGE
        {
            public nuint Begin;
            public nuint End;
        }

        /// <summary>
        /// Creates a D3D12 device + direct command queue on the first hardware adapter that
        /// supports feature level 11.0. Returns false (with a reason) on any failure — the caller
        /// falls back to CPU. On success the caller owns the three returned COM references and
        /// must release them via <see cref="Release"/> after the GRContext is disposed.
        /// </summary>
        public static bool TryCreateDevice(out IntPtr adapter, out IntPtr device, out IntPtr queue, out string failureReason)
        {
            adapter = IntPtr.Zero;
            device = IntPtr.Zero;
            queue = IntPtr.Zero;
            failureReason = null;

            IntPtr factory = IntPtr.Zero;
            try
            {
                fixed (Guid* riid = &IID_IDXGIFactory1)
                {
                    int hr = CreateDXGIFactory1(riid, &factory);
                    if (hr < 0)
                    {
                        failureReason = $"CreateDXGIFactory1 failed (0x{hr:X8}).";
                        return false;
                    }
                }

                for (uint i = 0; ; i++)
                {
                    IntPtr candidate;
                    int hr = ComCall(factory, VtblEnumAdapters1, i, &candidate);
                    if ((uint)hr == DxgiErrorNotFound)
                    {
                        failureReason = "No hardware D3D12 adapter found.";
                        return false;
                    }

                    if (hr < 0)
                    {
                        failureReason = $"EnumAdapters1 failed (0x{hr:X8}).";
                        return false;
                    }

                    // Skip software adapters (Microsoft Basic Render Driver / WARP) — the CPU
                    // Skia backend is faster and simpler than WARP for compositing.
                    DXGI_ADAPTER_DESC1 desc = default;
                    hr = ComCall(candidate, VtblGetDesc1, &desc);
                    if (hr < 0 || (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        Release(candidate);
                        continue;
                    }

                    IntPtr dev;
                    fixed (Guid* riid = &IID_ID3D12Device)
                        hr = D3D12CreateDevice(candidate, FeatureLevel11_0, riid, &dev);
                    if (hr < 0)
                    {
                        Release(candidate);
                        continue;
                    }

                    hr = CreateCommandQueue(dev, CommandListTypeDirect, out var q);
                    if (hr < 0)
                    {
                        Release(dev);
                        Release(candidate);
                        failureReason = $"CreateCommandQueue failed (0x{hr:X8}).";
                        return false;
                    }

                    adapter = candidate;
                    device = dev;
                    queue = q;
                    return true;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                failureReason = "D3D12/DXGI not available: " + ex.Message;
                return false;
            }
            finally
            {
                if (factory != IntPtr.Zero)
                    Release(factory);
            }
        }

        /// <summary>IUnknown::Release on a raw COM pointer.</summary>
        public static void Release(IntPtr comObject)
        {
            if (comObject == IntPtr.Zero)
                return;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)comObject)[VtblRelease];
            fn(comObject);
        }

        /// <summary>IUnknown::AddRef on a raw COM pointer; returns the new reference count.</summary>
        public static uint AddRef(IntPtr comObject)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)comObject)[VtblAddRef];
            return fn(comObject);
        }

        /// <summary>IUnknown::QueryInterface on a raw COM pointer. The result is AddRef'd, and
        /// null when the object does not implement the interface.</summary>
        public static int QueryInterface(IntPtr comObject, in Guid iid, out IntPtr result)
        {
            IntPtr r;
            int hr;
            fixed (Guid* riid = &iid)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)comObject)[VtblQueryInterface];
                hr = fn(comObject, riid, &r);
            }
            result = hr < 0 ? IntPtr.Zero : r;
            return hr;
        }

        /// <summary>Closes a Win32 handle — a shared-resource NT handle once the other API has
        /// opened it (the opened object holds its own reference to the allocation).</summary>
        public static void CloseSharedHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }

        // ------------------------------------------------------------------------ ID3D12Device

        /// <summary>ID3D12Device::CreateCommandQueue of the given list type, normal priority.</summary>
        public static int CreateCommandQueue(IntPtr device, int type, out IntPtr queue)
        {
            var desc = new D3D12_COMMAND_QUEUE_DESC { Type = type };
            IntPtr q;
            int hr;
            fixed (Guid* riid = &IID_ID3D12CommandQueue)
                hr = ComCall(device, VtblCreateCommandQueue, &desc, riid, &q);
            queue = hr < 0 ? IntPtr.Zero : q;
            return hr;
        }

        /// <summary>ID3D12Device::CreateCommandAllocator.</summary>
        public static int CreateCommandAllocator(IntPtr device, int type, out IntPtr allocator)
        {
            IntPtr a;
            int hr;
            fixed (Guid* riid = &IID_ID3D12CommandAllocator)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)(*(void***)device)[VtblCreateCommandAllocator];
                hr = fn(device, type, riid, &a);
            }
            allocator = hr < 0 ? IntPtr.Zero : a;
            return hr;
        }

        /// <summary>ID3D12Device::CreateCommandList (node 0, no pipeline state). The list is
        /// returned open, ready to record.</summary>
        public static int CreateCommandList(IntPtr device, int type, IntPtr allocator, out IntPtr list)
        {
            IntPtr l;
            int hr;
            fixed (Guid* riid = &IID_ID3D12GraphicsCommandList)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)device)[VtblCreateCommandList];
                hr = fn(device, 0, type, allocator, IntPtr.Zero, riid, &l);
            }
            list = hr < 0 ? IntPtr.Zero : l;
            return hr;
        }

        /// <summary>ID3D12Device::CreateCommittedResource on a heap of the given type, with no
        /// optimized clear value.</summary>
        public static int CreateCommittedResource(IntPtr device, int heapType, int heapFlags,
            in D3D12_RESOURCE_DESC desc, int initialState, out IntPtr resource)
        {
            var heap = new D3D12_HEAP_PROPERTIES { Type = heapType };
            IntPtr r;
            int hr;
            fixed (Guid* riid = &IID_ID3D12Resource)
            fixed (D3D12_RESOURCE_DESC* d = &desc)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D12_HEAP_PROPERTIES*, int, D3D12_RESOURCE_DESC*, int, IntPtr, Guid*, IntPtr*, int>)(*(void***)device)[VtblCreateCommittedResource];
                hr = fn(device, &heap, heapFlags, d, initialState, IntPtr.Zero, riid, &r);
            }
            resource = hr < 0 ? IntPtr.Zero : r;
            return hr;
        }

        /// <summary>ID3D12Device::CreateSharedHandle: an NT handle (GENERIC_ALL, unnamed, not
        /// inheritable) for a resource created on a <see cref="HeapFlagShared"/> heap or a
        /// <see cref="FenceFlagShared"/> fence, for another device or API to open (Direct3D 11's
        /// <c>OpenSharedResource1</c> / <c>OpenSharedFence</c>). Close it with
        /// <see cref="CloseSharedHandle"/> once it has been opened.</summary>
        public static int CreateSharedHandle(IntPtr device, IntPtr deviceChild, out IntPtr handle)
        {
            IntPtr h;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, IntPtr, IntPtr*, int>)(*(void***)device)[VtblCreateSharedHandle];
            int hr = fn(device, deviceChild, IntPtr.Zero, GenericAll, IntPtr.Zero, &h);
            handle = hr < 0 ? IntPtr.Zero : h;
            return hr;
        }

        /// <summary>ID3D12Device::GetAdapterLuid — the LUID of the adapter the device was created
        /// on, as one 64-bit value (LowPart in the low dword). A struct-returning COM method: on
        /// Windows the vtable entry takes a hidden return pointer after <c>this</c>.</summary>
        public static long GetAdapterLuid(IntPtr device)
        {
            long luid;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, long*, long*>)(*(void***)device)[VtblGetAdapterLuid];
            fn(device, &luid);
            return luid;
        }

        /// <summary>ID3D12Device::CreateFence with initial value 0.</summary>
        public static int CreateFence(IntPtr device, int flags, out IntPtr fence)
        {
            IntPtr f;
            int hr;
            fixed (Guid* riid = &IID_ID3D12Fence)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int, Guid*, IntPtr*, int>)(*(void***)device)[VtblCreateFence];
                hr = fn(device, 0, flags, riid, &f);
            }
            fence = hr < 0 ? IntPtr.Zero : f;
            return hr;
        }

        /// <summary>ID3D12Device::GetCopyableFootprints for subresource 0 of <paramref name="desc"/>:
        /// the placed footprint (row pitch padded to D3D12_TEXTURE_DATA_PITCH_ALIGNMENT = 256),
        /// row count, unpadded row size and total buffer size a readback of it needs.</summary>
        public static void GetCopyableFootprints(IntPtr device, in D3D12_RESOURCE_DESC desc,
            out D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint, out uint numRows, out ulong rowSizeBytes, out ulong totalBytes)
        {
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT f;
            uint rows;
            ulong rowSize, total;
            fixed (D3D12_RESOURCE_DESC* d = &desc)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D12_RESOURCE_DESC*, uint, uint, ulong, D3D12_PLACED_SUBRESOURCE_FOOTPRINT*, uint*, ulong*, ulong*, void>)(*(void***)device)[VtblGetCopyableFootprints];
                fn(device, d, 0, 1, 0, &f, &rows, &rowSize, &total);
            }
            footprint = f;
            numRows = rows;
            rowSizeBytes = rowSize;
            totalBytes = total;
        }

        // ------------------------------------------------------------------ ID3D12CommandQueue

        /// <summary>ID3D12CommandQueue::ExecuteCommandLists with a single closed list.</summary>
        public static void ExecuteCommandList(IntPtr queue, IntPtr list)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, void>)(*(void***)queue)[VtblExecuteCommandLists];
            fn(queue, 1, &list);
        }

        /// <summary>ID3D12CommandQueue::Signal — the queue sets the fence to <paramref name="value"/>
        /// once all work queued before this call has completed.</summary>
        public static int QueueSignal(IntPtr queue, IntPtr fence, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)(*(void***)queue)[VtblQueueSignal];
            return fn(queue, fence, value);
        }

        /// <summary>ID3D12CommandQueue::Wait — the queue stalls (on the GPU, not the CPU) until the
        /// fence reaches <paramref name="value"/>.</summary>
        public static int QueueWait(IntPtr queue, IntPtr fence, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)(*(void***)queue)[VtblQueueWait];
            return fn(queue, fence, value);
        }

        // ------------------------------------------------------------------------- ID3D12Fence

        /// <summary>ID3D12Fence::GetCompletedValue (UINT64_MAX after device removal).</summary>
        public static ulong FenceGetCompletedValue(IntPtr fence)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong>)(*(void***)fence)[VtblFenceGetCompletedValue];
            return fn(fence);
        }

        /// <summary>ID3D12Fence::SetEventOnCompletion — the Win32 event is set when the fence
        /// reaches <paramref name="value"/> (immediately if it already has).</summary>
        public static int FenceSetEventOnCompletion(IntPtr fence, ulong value, IntPtr eventHandle)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)(*(void***)fence)[VtblFenceSetEventOnCompletion];
            return fn(fence, value, eventHandle);
        }

        /// <summary>ID3D12Fence::Signal — sets the fence to <paramref name="value"/> from the CPU,
        /// at once, standing in for a GPU signal that will never come (a queue's Signal is the
        /// usual way; this is for recovery paths only). The value is written as given, so a
        /// caller must not move a fence backwards.</summary>
        public static int FenceSignal(IntPtr fence, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)(*(void***)fence)[VtblFenceSignal];
            return fn(fence, value);
        }

        // ---------------------------------------------------------------------- ID3D12Resource

        /// <summary>ID3D12Resource::Map of subresource 0 with no read-range restriction (the CPU
        /// may read all of it). Readback heaps may stay mapped for the resource's lifetime.</summary>
        public static int ResourceMap(IntPtr resource, out void* address)
        {
            void* p;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, D3D12_RANGE*, void**, int>)(*(void***)resource)[VtblResourceMap];
            int hr = fn(resource, 0, null, &p);
            address = hr < 0 ? null : p;
            return hr;
        }

        /// <summary>ID3D12Resource::Unmap of subresource 0 with an empty written range — the CPU
        /// never writes a readback heap.</summary>
        public static void ResourceUnmap(IntPtr resource)
        {
            var written = new D3D12_RANGE();
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, D3D12_RANGE*, void>)(*(void***)resource)[VtblResourceUnmap];
            fn(resource, 0, &written);
        }

        // ----------------------------------------------------------- ID3D12GraphicsCommandList

        /// <summary>ID3D12GraphicsCommandList::Close.</summary>
        public static int CommandListClose(IntPtr list)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)list)[VtblCommandListClose];
            return fn(list);
        }

        /// <summary>ID3D12GraphicsCommandList::CopyTextureRegion of the whole source (no box) to
        /// the destination's origin.</summary>
        public static void CommandListCopyTextureRegion(IntPtr list, in D3D12_TEXTURE_COPY_LOCATION dst, in D3D12_TEXTURE_COPY_LOCATION src)
        {
            fixed (D3D12_TEXTURE_COPY_LOCATION* d = &dst)
            fixed (D3D12_TEXTURE_COPY_LOCATION* s = &src)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D12_TEXTURE_COPY_LOCATION*, uint, uint, uint, D3D12_TEXTURE_COPY_LOCATION*, IntPtr, void>)(*(void***)list)[VtblCopyTextureRegion];
                fn(list, d, 0, 0, 0, s, IntPtr.Zero);
            }
        }

        // Raw vtable dispatch — one helper per arity used by device creation.
        private static int ComCall(IntPtr self, int slot, uint a0, IntPtr* a1)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)self)[slot];
            return fn(self, a0, a1);
        }

        private static int ComCall(IntPtr self, int slot, DXGI_ADAPTER_DESC1* a0)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)(*(void***)self)[slot];
            return fn(self, a0);
        }

        private static int ComCall(IntPtr self, int slot, D3D12_COMMAND_QUEUE_DESC* a0, Guid* a1, IntPtr* a2)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, D3D12_COMMAND_QUEUE_DESC*, Guid*, IntPtr*, int>)(*(void***)self)[slot];
            return fn(self, a0, a1, a2);
        }
    }
}
