using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Clowd.Com.Video
{
    [ComVisible(true)]
    [Guid("caf8820a-c854-4733-9c24-a7415f694ba8")]
    [AMovieSetup(Merit.DoNotUse, AMovieSetup.CLSID_VideoInputDeviceCategory)]
    public class CVideoSource : BaseSourceFilter, IAMFilterMiscFlags
    {
        public CVideoSource() : base("clowd-test")
        {
            AddPin(new CVideoPin(this));
        }

        protected override int OnInitializePins()
        {
            return S_OK;
        }

        public int GetMiscFlags()
        {
            return (int)AMFilterMiscFlags.IsSource;
        }
    }

    [ComVisible(false)]
    public class CVideoPin : SourceStream, IAMStreamConfig, IKsPropertySet
    {
        // media type properties
        protected int mt_bitCount = 32;
        protected int mt_captureWidth = 0;
        protected int mt_captureHeight = 0;
        protected long mt_avgTimePerFrame = UNITS / 30;
        private GDI32.BitmapInfo mt_bmi;

        // clock sync properties
        protected IReferenceClockImpl m_pClock = null;
        protected Semaphore m_hSemaphore = null;
        protected int m_dwAdviseToken = 0;
        protected long m_rtClockStart = 0;

        // capture properties
        IntPtr _srcContext = IntPtr.Zero;
        IntPtr _destContext = IntPtr.Zero;

        public CVideoPin(CVideoSource filter) : base("clowd-test-pin", filter)
        {
            mt_bmi = new GDI32.BitmapInfo();
            mt_bmi.bmiHeader = new BitmapInfoHeader();

            // create default mediatype
            GetMediaType(ref m_mt);
        }

        public override int Active()
        {
            //m_rtStart = 0;
            //m_bStartNotified = false;
            //m_bStopNotified = false;
            lock (m_Filter.FilterLock)
            {
                m_pClock = m_Filter.Clock;
                if (m_pClock.IsValid)
                {
                    m_pClock._AddRef();
                    m_hSemaphore = new Semaphore(0, 0x7FFFFFFF);
                }
            }

            if (_srcContext == IntPtr.Zero)
                _srcContext = USER32.GetWindowDC(IntPtr.Zero);

            if (_destContext == IntPtr.Zero)
                _destContext = GDI32.CreateCompatibleDC(_srcContext);

            return base.Active();
        }

        public override int Inactive()
        {
            if (_srcContext != IntPtr.Zero)
            {
                USER32.ReleaseDC(IntPtr.Zero, _srcContext);
                _srcContext = IntPtr.Zero;
            }
            if (_destContext != IntPtr.Zero)
            {
                GDI32.DeleteDC(_destContext);
                _destContext = IntPtr.Zero;
            }

            HRESULT hr = (HRESULT)base.Inactive();
            if (m_pClock != null)
            {
                if (m_dwAdviseToken != 0)
                {
                    m_pClock.Unadvise(m_dwAdviseToken);
                    m_dwAdviseToken = 0;
                }
                m_pClock._Release();
                m_pClock = null;
                if (m_hSemaphore != null)
                {
                    m_hSemaphore.Close();
                    m_hSemaphore = null;
                }
            }
            return hr;
        }

        public override int Notify(IntPtr pSelf, Quality q)
        {
            // no plans to implement...
            return E_FAIL;
        }

        public override int GetMediaType(ref AMMediaType pMediaType)
        {
            GetDefaultCaps(out var _caps);

            int nWidth = 0;
            int nHeight = 0;

            if (pMediaType == null)
                pMediaType = new AMMediaType();

            if (m_mt != null && m_mt.majorType == MediaType.Video)
            {
                // our mediatype already exists, lets return it
                pMediaType.Set(m_mt);
                return S_OK;
            }

            // the media type has not yet been set, lets create and set a default.
            pMediaType.majorType = MediaType.Video;
            pMediaType.formatType = FormatType.VideoInfo;

            VideoInfoHeader vih = new VideoInfoHeader();
            vih.AvgTimePerFrame = mt_avgTimePerFrame;
            vih.BmiHeader.Compression = BI_RGB;
            vih.BmiHeader.BitCount = (short)mt_bitCount;
            vih.BmiHeader.Width = nWidth;
            vih.BmiHeader.Height = nHeight;
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

        public override int SetMediaType(AMMediaType pmt)
        {
            if (pmt == null) return E_POINTER;
            if (pmt.formatPtr == IntPtr.Zero) return VFW_E_INVALIDMEDIATYPE;
            HRESULT hr = (HRESULT)CheckMediaType(pmt);
            if (hr.Failed) return hr;

            hr = (HRESULT)base.SetMediaType(pmt);
            if (hr.Failed) return hr;

            //if (m_pProperties != null)
            //{
            //    SuggestAllocatorProperties(m_pProperties);
            //}

            lock (m_Lock)
            {
                BitmapInfoHeader _bmi = pmt;
                mt_bmi.bmiHeader.BitCount = _bmi.BitCount;
                if (_bmi.Height > 0) mt_bmi.bmiHeader.Height = _bmi.Height;
                if (_bmi.Width > 0) mt_bmi.bmiHeader.Width = _bmi.Width;
                mt_bmi.bmiHeader.Compression = BI_RGB;
                mt_bmi.bmiHeader.Planes = 1;
                mt_bmi.bmiHeader.ImageSize = ALIGN16(mt_bmi.bmiHeader.Width) * ALIGN16(Math.Abs(mt_bmi.bmiHeader.Height)) * mt_bmi.bmiHeader.BitCount / 8;
                mt_captureWidth = _bmi.Width;
                mt_captureHeight = _bmi.Height;
                mt_bitCount = _bmi.BitCount;

                VideoInfoHeader _pvi = pmt;
                if (_pvi != null)
                {
                    mt_avgTimePerFrame = _pvi.AvgTimePerFrame;
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

            GetDefaultCaps(out var _caps);
            if (_bmi.Width < _caps.MinOutputSize.Width || _bmi.Width > _caps.MaxOutputSize.Width)
                return VFW_E_INVALIDMEDIATYPE;

            long _rate = 0;
            VideoInfoHeader _pvi = pmt;
            if (_pvi != null)
            {
                _rate = _pvi.AvgTimePerFrame;
            }

            if (_rate < _caps.MinFrameInterval || _rate > _caps.MaxFrameInterval)
            {
                return VFW_E_INVALIDMEDIATYPE;
            }

            return NOERROR;
        }

        public override int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties prop)
        {
            AllocatorProperties _actual = new AllocatorProperties();
            BitmapInfoHeader _bmi = m_mt;

            prop.cbBuffer = _bmi.GetBitmapSize();

            if (prop.cbBuffer < _bmi.ImageSize)
            {
                prop.cbBuffer = _bmi.ImageSize;
            }

            if (prop.cbBuffer < mt_bmi.bmiHeader.ImageSize)
            {
                prop.cbBuffer = mt_bmi.bmiHeader.ImageSize;
            }

            prop.cBuffers = 1;
            prop.cbAlign = 1;
            prop.cbPrefix = 0;
            int hr = pAlloc.SetProperties(prop, _actual);
            return hr;
        }

        public override int FillBuffer(ref IMediaSampleImpl _sample)
        {
            HRESULT hr = NOERROR;
            long rtLatency = mt_avgTimePerFrame;

            if (m_dwAdviseToken == 0)
            {
                // set up clock advise. this will signal the semaphore every 'rtLatency'
                m_pClock.GetTime(out m_rtClockStart);
                hr = (HRESULT)m_pClock.AdvisePeriodic(m_rtClockStart + rtLatency, rtLatency, m_hSemaphore.Handle, out m_dwAdviseToken);
                hr.Assert();
            }
            else
            {
                ASSERT(m_hSemaphore.WaitOne());
            }

            FillBufferImpl(ref _sample);

            m_pClock.GetTime(out var frameEndTime);

            _sample.SetTime(m_rtClockStart, frameEndTime);
            _sample.SetActualDataLength(_sample.GetSize());
            _sample.SetSyncPoint(true); // true for every uncompressed frame (msdn)

            throw new NotImplementedException();
        }

        public void FillBufferImpl(ref IMediaSampleImpl _sample)
        {
            IntPtr _ptr;
            _sample.GetPointer(out _ptr);

            int _captureX = 0, _captureY = 0;

            IntPtr destBitmap = GDI32.CreateCompatibleBitmap(_srcContext, mt_captureWidth, mt_captureHeight);
            IntPtr hOld = GDI32.SelectObject(_destContext, destBitmap);
            GDI32.BitBlt(_destContext, 0, 0, mt_captureWidth, mt_captureHeight, _srcContext, _captureX, _captureY, GDI32.SRCCOPY /* | GDI32.CAPTUREBLT*/);
            GDI32.SelectObject(_destContext, hOld);
            GDI32.GetDIBits(_destContext, destBitmap, 0, (uint)Math.Abs(mt_captureHeight), _ptr, ref mt_bmi, 0);
            GDI32.DeleteObject(destBitmap);
        }

        public int SetFormat([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt)
        {
            if (m_Filter.IsActive) return VFW_E_WRONG_STATE;
            HRESULT hr;
            AMMediaType _newType = new AMMediaType(pmt);
            AMMediaType _oldType = new AMMediaType(m_mt);
            hr = (HRESULT)CheckMediaType(_newType);
            if (FAILED(hr)) return hr;
            m_mt.Set(_newType);
            if (IsConnected)
            {
                hr = (HRESULT)Connected.QueryAccept(_newType);
                if (SUCCEEDED(hr))
                {
                    hr = (HRESULT)m_Filter.ReconnectPin(this, _newType);
                    if (SUCCEEDED(hr))
                    {
                        SetMediaType(_newType);
                    }
                    else
                    {
                        m_mt.Set(_oldType);
                        m_Filter.ReconnectPin(this, _oldType);
                    }
                }
            }
            else
            {
                SetMediaType(_newType);
            }
            return hr;
        }

        public int GetFormat([Out] out AMMediaType pmt)
        {
            pmt = new AMMediaType(m_mt);
            return NOERROR;
        }

        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
        {
            var iCount = 0;
            AMMediaType mt = new AMMediaType();
            while (GetMediaType(iCount, ref mt) == S_OK) { mt.Free(); iCount++; };
            var iSize = Marshal.SizeOf(typeof(VideoStreamConfigCaps));

            if (piCount != IntPtr.Zero)
                Marshal.WriteInt32(piCount, iCount);

            if (piSize != IntPtr.Zero)
                Marshal.WriteInt32(piSize, iSize);

            return S_OK;
        }

        public int GetStreamCaps([In] int iIndex, [In, Out] IntPtr ppmt, [In] IntPtr pSCC)
        {
            // check if media type at this iIndex exists....
            AMMediaType pmt = null;
            HRESULT hr = (HRESULT)GetMediaType(iIndex, ref pmt);
            if (FAILED(hr)) return hr;
            if (hr == VFW_S_NO_MORE_ITEMS) return S_FALSE;

            // get default capabilities
            GetDefaultCaps(out var caps);

            // write capabilities and media type to provided pointers
            if (ppmt != IntPtr.Zero)
            {
                IntPtr _ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf(pmt));
                Marshal.StructureToPtr(pmt, _ptr, true);
                Marshal.WriteIntPtr(ppmt, _ptr);
            }

            if (pSCC != IntPtr.Zero)
            {
                Marshal.StructureToPtr(caps, pSCC, false);
            }

            return S_OK;
        }

        public void GetDefaultCaps(out VideoStreamConfigCaps _caps)
        {
            _caps = new VideoStreamConfigCaps();

            _caps.guid = FormatType.VideoInfo;
            _caps.VideoStandard = AnalogVideoStandard.None;

            _caps.InputSize.Width = mt_captureWidth;
            _caps.InputSize.Height = mt_captureHeight;

            _caps.MinCroppingSize.Width = 320;
            _caps.MinCroppingSize.Height = 240;
            _caps.MaxCroppingSize.Width = SystemInformation.VirtualScreen.Width;
            _caps.MaxCroppingSize.Height = SystemInformation.VirtualScreen.Height;
            _caps.CropGranularityX = 1;
            _caps.CropGranularityY = 1;
            _caps.CropAlignX = 0;
            _caps.CropAlignY = 0;

            _caps.MinOutputSize.Width = _caps.MinCroppingSize.Width;
            _caps.MinOutputSize.Height = _caps.MinCroppingSize.Height;
            _caps.MaxOutputSize.Width = _caps.MaxCroppingSize.Width;
            _caps.MaxOutputSize.Height = _caps.MaxCroppingSize.Height;
            _caps.OutputGranularityX = _caps.CropGranularityX;
            _caps.OutputGranularityY = _caps.CropGranularityY;

            _caps.StretchTapsX = 0;
            _caps.StretchTapsY = 0;
            _caps.ShrinkTapsX = 0;
            _caps.ShrinkTapsY = 0;


            const int minFPS = 4;
            const int maxFPS = 60;

            _caps.MinFrameInterval = UNITS / maxFPS; // this is the reference INTERVAL, not FPS.. so max/min reversed
            _caps.MaxFrameInterval = UNITS / minFPS;
            _caps.MinBitsPerSecond = (_caps.MinOutputSize.Width * _caps.MinOutputSize.Height * mt_bitCount) * minFPS; //(minfps)
            _caps.MaxBitsPerSecond = (_caps.MaxOutputSize.Width * _caps.MaxOutputSize.Height * mt_bitCount) * maxFPS; //(maxfps)
        }

        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

        public int Set([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In] IntPtr pPropData, [In] int cbPropData)
        {
            return E_NOTIMPL;
        }

        public int Get([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In, Out] IntPtr pPropData, [In] int cbPropData, [Out] out int pcbReturned)
        {
            pcbReturned = Marshal.SizeOf(typeof(Guid));
            if (guidPropSet != PropSetID.Pin)
            {
                return E_PROP_SET_UNSUPPORTED;
            }
            if (dwPropID != (int)AMPropertyPin.Category)
            {
                return E_PROP_ID_UNSUPPORTED;
            }
            if (pPropData == IntPtr.Zero)
            {
                return NOERROR;
            }
            if (cbPropData < Marshal.SizeOf(typeof(Guid)))
            {
                return E_UNEXPECTED;
            }
            Marshal.StructureToPtr(PinCategory.Capture, pPropData, false);
            return NOERROR;
        }

        public int QuerySupported([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [Out] out KSPropertySupport pTypeSupport)
        {
            pTypeSupport = KSPropertySupport.Get;
            if (guidPropSet != PropSetID.Pin)
            {
                return E_PROP_SET_UNSUPPORTED;
            }
            if (dwPropID != (int)AMPropertyPin.Category)
            {
                return E_PROP_ID_UNSUPPORTED;
            }
            return S_OK;
        }
    }
}
