using DirectShow;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Device = SharpDX.Direct3D11.Device;
using ResultCode = SharpDX.DXGI.ResultCode;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Sonic;

namespace Clowd.Com.Video
{
    class DxgiFrameProvider : FrameProviderBase
    {
        public static Adapter[] Adapters
        {
            get
            {
                using (var factory = new Factory1())
                    return factory.Adapters;
            }
        }

        // dxgi setup
        Factory1 factory;
        Adapter1 adapter;
        Device device;
        Output output;
        Output1 output1;

        // duplication
        Texture2DDescription textureDesc;
        Texture2D screenTexture;
        OutputDuplication duplicatedOutput;

        // frames
        bool needsRelease;
        bool preBufferedFrame;
        SharpDX.DXGI.Resource screenResource = null;
        OutputDuplicateFrameInformation frameInfo = default(OutputDuplicateFrameInformation);

        public DxgiFrameProvider(int adapterNum, int outputNum, int maxTextures, bool useAquireLock)
        {
            factory = new Factory1();
            adapter = factory.GetAdapter1(adapterNum);
            device = new Device(adapter);
            output = adapter.GetOutput(outputNum);
            output1 = output.QueryInterface<Output1>();
            InitializeDuplication();
        }

        private void InitializeDuplication()
        {
            if (screenResource != null)
                screenResource.Dispose();

            if (duplicatedOutput != null)
                duplicatedOutput.Dispose();

            if (screenTexture != null)
                screenTexture.Dispose();

            needsRelease = false;
            preBufferedFrame = false;
            var nBounds = output.Description.DesktopBounds;
            int width = nBounds.Right - nBounds.Left;
            int height = nBounds.Bottom - nBounds.Top;

            textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging,
            };

            screenTexture = new Texture2D(device, textureDesc);
            duplicatedOutput = output1.DuplicateOutput(device);
        }

        //private void ReleaseFrame()
        //{
        //    if (!needsRelease)
        //        return;

        //    try
        //    {
        //        duplicatedOutput.ReleaseFrame();
        //    }
        //    catch (SharpDXException e)
        //    {
        //        if (e.ResultCode == ResultCode.InvalidCall)
        //        {
        //            // frame was already released
        //            return;
        //        }
        //        else if (e.ResultCode == ResultCode.AccessLost)
        //        {
        //            // we lost access to the desktop, lets re-init
        //            InitializeDuplication();
        //        }
        //        else
        //        {
        //            throw;
        //        }
        //    }
        //}

        public override void Dispose()
        {
            output1.Dispose();
            output.Dispose();
            device.Dispose();
            adapter.Dispose();
            factory.Dispose();
            screenResource.Dispose();
            duplicatedOutput.Dispose();
            screenTexture.Dispose();
        }

        public override int SetCaptureProperties(CaptureProperties properties)
        {
            var hr = base.SetCaptureProperties(properties);
            InitializeDuplication();
            return hr;
        }

        public override int CopyScreenToSamplePtr(ref IMediaSampleImpl _sample)
        {
            var nBounds = output.Description.DesktopBounds;
            int srcWidth = nBounds.Right - nBounds.Left;
            int srcHeight = nBounds.Bottom - nBounds.Top;

            // loop until we get a non-empty frame
            duplicatedOutput.AcquireNextFrame(1000, out frameInfo, out screenResource);
            while (frameInfo.TotalMetadataBufferSize <= 0 || frameInfo.LastPresentTime <= 0)
            {
                // This is how you wait for an image containing image data according to SO (https://stackoverflow.com/questions/49481467/acquirenextframe-not-working-desktop-duplication-api-d3d11)
                try
                {
                    duplicatedOutput.ReleaseFrame();
                    duplicatedOutput.AcquireNextFrame(1000, out frameInfo, out screenResource);
                }
                catch (SharpDXException e)
                {
                    if (e.ResultCode == ResultCode.AccessLost)
                    {
                        // we lost access to the desktop, lets re-init
                        InitializeDuplication();
                        continue;
                    }
                    else if (e.ResultCode == ResultCode.WaitTimeout)
                    {
                        continue;
                    }
                    else
                    {
                        return COMHelper.E_FAIL;
                    }
                }
            }

            // copy resource into memory that can be accessed by the CPU
            using (var screenTexture2D = screenResource.QueryInterface<Texture2D>())
                device.ImmediateContext.CopyResource(screenTexture2D, screenTexture);

            // Get the desktop capture texture
            var mapSource = device.ImmediateContext.MapSubresource(screenTexture, 0, MapMode.Read, MapFlags.None);
            var sourcePtr = mapSource.DataPointer;

            IntPtr destPtr;
            _sample.GetPointer(out destPtr);
            
            int stride = 4 * ((_properties.PixelWidth * 4 + 3) / 4);
            //throw new Exception($"bits: {_properties.BitCount} stride: {stride} source: {nBounds.Left},{nBounds.Top},{srcWidth},{srcHeight}-{mapSource.RowPitch}, dest: {_properties.X},{_properties.Y},{_properties.PixelWidth},{_properties.PixelHeight}");

            for (int y = 0; y < srcHeight; y++)
            {
                // Copy a single line 
                Utilities.CopyMemory(destPtr, sourcePtr, srcWidth * 4);

                // Advance pointers
                sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                destPtr = IntPtr.Add(destPtr, stride);
            }

            // clean up
            device.ImmediateContext.UnmapSubresource(screenTexture, 0);
            screenResource.Dispose();
            duplicatedOutput.ReleaseFrame();

            return COMHelper.S_OK;
        }

        public Bitmap CaptureBitmap()
        {
            var nBounds = output.Description.DesktopBounds;
            int width = nBounds.Right - nBounds.Left;
            int height = nBounds.Bottom - nBounds.Top;

            var textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = width,
                Height = height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging,
            };

            var screenTexture = new Texture2D(device, textureDesc);
            var duplicatedOutput = output1.DuplicateOutput(device);

            // loop until we get a non-empty frame
            SharpDX.DXGI.Resource screenResource = null;
            OutputDuplicateFrameInformation frameInfo = default(OutputDuplicateFrameInformation);
            duplicatedOutput.AcquireNextFrame(1000, out frameInfo, out screenResource);
            do
            {
                // This is how you wait for an image containing image data according to SO (https://stackoverflow.com/questions/49481467/acquirenextframe-not-working-desktop-duplication-api-d3d11)
                try
                {
                    duplicatedOutput.ReleaseFrame();
                    duplicatedOutput.AcquireNextFrame(1000, out frameInfo, out screenResource);
                }
                catch (SharpDXException e)
                {
                    int WAIT_TIMEOUT = SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code;
                    //int ACCESS_LOST = SharpDX.DXGI.ResultCode.AccessLost.Result.Code; // TODO for screen capture...

                    if (e.ResultCode == WAIT_TIMEOUT)
                        continue;

                    throw;
                }
            } while (frameInfo.TotalMetadataBufferSize <= 0 || frameInfo.LastPresentTime <= 0);

            // copy resource into memory that can be accessed by the CPU
            using (var screenTexture2D = screenResource.QueryInterface<Texture2D>())
                device.ImmediateContext.CopyResource(screenTexture2D, screenTexture);

            // Get the desktop capture texture
            var mapSource = device.ImmediateContext.MapSubresource(screenTexture, 0, MapMode.Read, MapFlags.None);

            // Create Drawing.Bitmap
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var boundsRect = new Rectangle(0, 0, width, height);

            // Copy pixels from screen capture Texture to GDI bitmap
            var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
            var sourcePtr = mapSource.DataPointer;
            var destPtr = mapDest.Scan0;
            for (int y = 0; y < height; y++)
            {
                // Copy a single line 
                Utilities.CopyMemory(destPtr, sourcePtr, width * 4);

                // Advance pointers
                sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                destPtr = IntPtr.Add(destPtr, mapDest.Stride);
            }

            // Release source and dest locks
            bitmap.UnlockBits(mapDest);
            device.ImmediateContext.UnmapSubresource(screenTexture, 0);

            screenResource.Dispose();
            duplicatedOutput.ReleaseFrame();
            screenTexture.Dispose();

            return bitmap;
        }
    }
}

