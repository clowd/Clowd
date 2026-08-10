using System;
using System.Runtime.InteropServices;

namespace Clowd.VideoSDK.Composition
{
    /// <summary>
    /// Minimal hand-rolled D3D12 device creation for the headless Skia GPU backend — just enough
    /// COM interop (factory → hardware adapter → device → direct command queue) to fill a
    /// <c>GRD3DBackendContext</c>. Deliberately no Windows SDK projection / TerraFX dependency:
    /// four calls do not justify a package. COM methods are invoked through raw vtable slots.
    /// </summary>
    internal static unsafe class D3D12Backend
    {
        // IUnknown:                     0 QueryInterface, 1 AddRef, 2 Release
        // IDXGIObject:                  3..6
        // IDXGIFactory:                 7 EnumAdapters .. 11 CreateSoftwareAdapter
        // IDXGIFactory1:                12 EnumAdapters1, 13 IsCurrent
        private const int VtblRelease = 2;
        private const int VtblEnumAdapters1 = 12;

        // IDXGIAdapter: 7 EnumOutputs, 8 GetDesc, 9 CheckInterfaceSupport; IDXGIAdapter1: 10 GetDesc1
        private const int VtblGetDesc1 = 10;

        // ID3D12Object: 3..6; ID3D12Device: 7 GetNodeCount, 8 CreateCommandQueue, ...
        private const int VtblCreateCommandQueue = 8;

        private const uint DxgiErrorNotFound = 0x887A0002;
        private const uint DxgiAdapterFlagSoftware = 2;
        private const int FeatureLevel11_0 = 0xb000;

        private static readonly Guid IID_ID3D12Device = new Guid("189819F1-1DB6-4B57-BE54-1821339B85F7");
        private static readonly Guid IID_IDXGIFactory1 = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");

        [DllImport("dxgi", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* ppFactory);

        [DllImport("d3d12", ExactSpelling = true)]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, Guid* riid, IntPtr* ppDevice);

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
            public int Type;     // D3D12_COMMAND_LIST_TYPE_DIRECT = 0
            public int Priority; // D3D12_COMMAND_QUEUE_PRIORITY_NORMAL = 0
            public int Flags;    // D3D12_COMMAND_QUEUE_FLAG_NONE = 0
            public uint NodeMask;
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

                    var queueDesc = new D3D12_COMMAND_QUEUE_DESC(); // direct, normal priority
                    IntPtr q;
                    fixed (Guid* riid = &IID_ID3D12CommandQueue)
                        hr = ComCall(dev, VtblCreateCommandQueue, &queueDesc, riid, &q);
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

        private static readonly Guid IID_ID3D12CommandQueue = new Guid("0EC870A6-5D7E-4C22-8CFC-5BAAE07616ED");

        /// <summary>IUnknown::Release on a raw COM pointer.</summary>
        public static void Release(IntPtr comObject)
        {
            if (comObject == IntPtr.Zero)
                return;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)comObject)[VtblRelease];
            fn(comObject);
        }

        // Raw vtable dispatch — one helper per arity used above.
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
