using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Clowd.Com.Video
{
    [ComVisible(false)]
    public class CVideoPin : SynchronizedSourceStream
    {
        const int FPS_MIN = 4;
        const int FPS_MAX = 60;
        const int FPS_DEFAULT = 30;

        private IFrameProvider m_frameProvider = null;

        public CVideoPin(string _name, BaseSourceFilter _filter) : base(_name, UNITS / FPS_DEFAULT, _filter)
        {
            m_frameProvider = new GdiFrameProvider();
            m_mt.majorType = Guid.Empty;
            GetMediaType(0, ref m_mt);
        }

        public override int SetMediaType(AMMediaType mt)
        {
            int hr = CheckMediaType(mt);
            if (FAILED(hr))
                return hr;

            hr = base.SetMediaType(mt);
            if (FAILED(hr))
                return hr;

            var pmt = mt;
            lock (m_Filter.FilterLock)
            {
                BitmapInfoHeader _bmi = pmt;
                var capt = new CaptureProperties()
                {
                    BitCount = _bmi.BitCount,
                    X = 0,
                    Y = 0,
                    PixelHeight = _bmi.Height,
                    PixelWidth = _bmi.Width,
                };

                m_frameProvider.SetCaptureProperties(capt);

                VideoInfoHeader _pvi = pmt;
                if (_pvi != null)
                {
                    SetLatency(_pvi.AvgTimePerFrame);
                }
            }
            return NOERROR;
        }

        public override int CheckMediaType(AMMediaType pmt)
        {
            if (pmt == null)
                return E_POINTER;

            if (pmt.formatPtr == IntPtr.Zero)
                return VFW_E_INVALIDMEDIATYPE;

            if (pmt.majorType != MediaType.Video)
                return VFW_E_INVALIDMEDIATYPE;

            if (pmt.subType != MediaSubType.RGB24 && pmt.subType != MediaSubType.RGB32 && pmt.subType != MediaSubType.ARGB32)
                return VFW_E_INVALIDMEDIATYPE;

            BitmapInfoHeader _bmi = pmt;
            if (_bmi == null)
                return E_UNEXPECTED;

            if (_bmi.Compression != BI_RGB)
                return VFW_E_TYPE_NOT_ACCEPTED;

            if (_bmi.BitCount != 24 && _bmi.BitCount != 32)
                return VFW_E_TYPE_NOT_ACCEPTED;

            // Check video size is within capabilities
            VideoStreamConfigCaps _caps;
            GetDefaultCaps(out _caps);
            if (_bmi.Width < _caps.MinOutputSize.Width || _bmi.Width > _caps.MaxOutputSize.Width)
                return VFW_E_INVALIDMEDIATYPE;

            // Check framerate is within capabilities
            long _rate = 0;
            VideoInfoHeader _pvi = pmt;
            if (_pvi != null)
                _rate = _pvi.AvgTimePerFrame;

            if (_rate < _caps.MinFrameInterval || _rate > _caps.MaxFrameInterval)
                return VFW_E_INVALIDMEDIATYPE;

            return NOERROR;
        }

        public override int GetMediaType(ref AMMediaType pMediaType)
        {
            VideoStreamConfigCaps _caps;
            GetDefaultCaps(out _caps);
            m_frameProvider.GetCaptureProperties(out var capt);

            if (CurrentMediaType.majorType == MediaType.Video)
            {
                // if CurrentMediaType has already been set, lets just return that. 
                pMediaType.Set(CurrentMediaType);
                return NOERROR;
            }

            // if not, lets return the default media type
            pMediaType.majorType = MediaType.Video;
            pMediaType.formatType = FormatType.VideoInfo;

            GetLatency(out var latency);

            VideoInfoHeader vih = new VideoInfoHeader();
            vih.AvgTimePerFrame = latency;
            vih.BmiHeader.Compression = BI_RGB;
            vih.BmiHeader.BitCount = capt.BitCount;
            vih.BmiHeader.Width = capt.PixelWidth;
            vih.BmiHeader.Height = capt.PixelHeight;
            vih.BmiHeader.Planes = 1;
            vih.BmiHeader.ImageSize = vih.BmiHeader.Width * Math.Abs(vih.BmiHeader.Height) * vih.BmiHeader.BitCount / 8;

            if (vih.BmiHeader.BitCount == 32)
            {
                pMediaType.subType = MediaSubType.RGB32;
            }

            if (vih.BmiHeader.BitCount == 24)
            {
                pMediaType.subType = MediaSubType.RGB24;
            }

            AMMediaType.SetFormat(ref pMediaType, ref vih);
            pMediaType.fixedSizeSamples = true;
            pMediaType.sampleSize = vih.BmiHeader.ImageSize;

            return NOERROR;
        }

        public override int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties allocRequest)
        {
            if (pAlloc == null)
                return E_POINTER;

            if (allocRequest == null)
                return E_POINTER;

            if (!IsConnected)
                return VFW_E_NOT_CONNECTED;

            m_frameProvider.GetCaptureProperties(out var capt);
            BitmapInfoHeader _bmi = CurrentMediaType;
            int maxSize = Math.Max(Math.Max(_bmi.GetBitmapSize(), _bmi.ImageSize), capt.Size);

            allocRequest.cbAlign = 1;
            allocRequest.cbPrefix = 0;
            allocRequest.cBuffers = 1;
            allocRequest.cbBuffer = maxSize;

            AllocatorProperties allocActual = new AllocatorProperties();
            var hr = pAlloc.SetProperties(allocRequest, allocActual);
            if (FAILED(hr))
                return hr;

            // we've asked the allocator for maxSize, but it may not have allocated the memory we requested.. so we need to check
            if (allocActual.cbBuffer < allocRequest.cbBuffer)
                return E_FAIL;

            return S_OK;
        }

        public override int FillBuffer(ref IMediaSampleImpl _sample)
        {
            int hr = S_OK;

            WaitFrameStart(out var frameStart);

            hr = m_frameProvider.CopyScreenToSamplePtr(ref _sample);
            if (FAILED(hr) || S_FALSE == hr) return hr;

            MarkFrameEnd(out var frameEnd);

            _sample.SetTime(frameStart, frameEnd);
            _sample.SetActualDataLength(_sample.GetSize());
            _sample.SetSyncPoint(true);

            return hr;
        }

        public override int GetDefaultCaps(out VideoStreamConfigCaps caps)
        {
            m_frameProvider.GetCaptureProperties(out var capt);

            caps = new VideoStreamConfigCaps();

            caps.guid = FormatType.VideoInfo;
            caps.VideoStandard = AnalogVideoStandard.None;

            caps.InputSize.Width = capt.PixelWidth;
            caps.InputSize.Height = capt.PixelHeight;

            caps.MinCroppingSize.Width = 320;
            caps.MinCroppingSize.Height = 240;
            caps.MaxCroppingSize.Width = SystemInformation.VirtualScreen.Width;
            caps.MaxCroppingSize.Height = SystemInformation.VirtualScreen.Height;
            caps.CropGranularityX = 1;
            caps.CropGranularityY = 1;
            caps.CropAlignX = 0;
            caps.CropAlignY = 0;

            caps.MinOutputSize.Width = caps.MinCroppingSize.Width;
            caps.MinOutputSize.Height = caps.MinCroppingSize.Height;
            caps.MaxOutputSize.Width = caps.MaxCroppingSize.Width;
            caps.MaxOutputSize.Height = caps.MaxCroppingSize.Height;
            caps.OutputGranularityX = caps.CropGranularityX;
            caps.OutputGranularityY = caps.CropGranularityY;

            caps.StretchTapsX = 0;
            caps.StretchTapsY = 0;
            caps.ShrinkTapsX = 0;
            caps.ShrinkTapsY = 0;

            caps.MinFrameInterval = UNITS / FPS_MAX; // this is the reference INTERVAL, so the min/max is reversed
            caps.MaxFrameInterval = UNITS / FPS_MIN;
            caps.MinBitsPerSecond = (caps.MinOutputSize.Width * caps.MinOutputSize.Height * 24) * FPS_MIN; //(minfps)
            caps.MaxBitsPerSecond = (caps.MaxOutputSize.Width * caps.MaxOutputSize.Height * 32) * FPS_MAX; //(maxfps)

            return NOERROR;
        }
    }
}
