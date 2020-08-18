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
    [ComVisible(true)]
    [Guid("caf8820a-c854-4743-9c24-a7415f694ba8")]
    [AMovieSetup(Merit.DoNotUse, AMovieSetup.CLSID_VideoInputDeviceCategory)]
    public class CVideoSource2 : BaseSourceFilter, IAMFilterMiscFlags
    {
        public CVideoSource2() : base("clowd-test2")
        {
            AddPin(new SourceFilterStream2("clowd-test2-pin", this));
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
    public class SourceFilterStream2 : SourceStream
                , IKsPropertySet
                , IAMStreamConfig
                , IAMBufferNegotiation
    {

        #region Constants

        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

        #endregion

        #region Variables

        protected object m_csPinLock = new object();
        protected long m_rtStart = 0;
        protected AllocatorProperties m_pProperties = null;
        protected IReferenceClockImpl m_pClock = null;
        protected int m_dwAdviseToken = 0;
        protected Semaphore m_hSemaphore = null;
        protected long m_rtClockStart = 0;
        protected long m_rtClockStop = 0;
        protected int _bitCount = 32;
        protected int _captureWidth = 0;
        protected int _captureHeight = 0;
        protected int _captureX = 0;
        protected int _captureY = 0;
        protected long _avgTimePerFrame = UNITS / 30;
        private GDI32.BitmapInfo m_bmi = new GDI32.BitmapInfo();
        private DateTime _lastMouseClick = DateTime.Now.AddSeconds(-5);
        private Point _lastMouseClickPosition = new Point(0, 0);
        IntPtr _srcContext = IntPtr.Zero;
        IntPtr _destContext = IntPtr.Zero;

        #endregion

        #region Constructor

        public SourceFilterStream2(string _name, BaseSourceFilter _filter)
            : base(_name, _filter)
        {
            m_bmi.bmiHeader = new BitmapInfoHeader();
            m_mt.majorType = Guid.Empty;
            GetMediaType(0, ref m_mt);
        }

        #endregion

        #region Overridden Methods

        public override int SetMediaType(AMMediaType mt)
        {
            if (mt == null) return E_POINTER;
            if (mt.formatPtr == IntPtr.Zero) return VFW_E_INVALIDMEDIATYPE;
            HRESULT hr = (HRESULT)CheckMediaType(mt);
            if (hr.Failed) return hr;
            hr = (HRESULT)base.SetMediaType(mt);
            if (hr.Failed) return hr;
            if (m_pProperties != null)
            {
                SuggestAllocatorProperties(m_pProperties);
            }

            var pmt = mt;
            lock (m_Filter.FilterLock)
            {
                BitmapInfoHeader _bmi = pmt;
                m_bmi.bmiHeader.BitCount = _bmi.BitCount;
                if (_bmi.Height != 0) m_bmi.bmiHeader.Height = _bmi.Height;
                if (_bmi.Width > 0) m_bmi.bmiHeader.Width = _bmi.Width;
                m_bmi.bmiHeader.Compression = BI_RGB;
                m_bmi.bmiHeader.Planes = 1;
                m_bmi.bmiHeader.ImageSize = ALIGN16(m_bmi.bmiHeader.Width) * ALIGN16(Math.Abs(m_bmi.bmiHeader.Height)) * m_bmi.bmiHeader.BitCount / 8;
                _captureWidth = _bmi.Width;
                _captureHeight = _bmi.Height;
                _bitCount = _bmi.BitCount;

                VideoInfoHeader _pvi = pmt;
                if (_pvi != null)
                {
                    _avgTimePerFrame = _pvi.AvgTimePerFrame;
                }
                _pvi = pmt;
                if (_pvi != null)
                {
                    _avgTimePerFrame = _pvi.AvgTimePerFrame;
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

            VideoStreamConfigCaps _caps;
            GetDefaultCaps(0, out _caps);
            if (_bmi.Width < _caps.MinOutputSize.Width || _bmi.Width > _caps.MaxOutputSize.Width)
                return VFW_E_INVALIDMEDIATYPE;
            long _rate = 0;
            VideoInfoHeader _pvi = pmt;
            if (_pvi != null)
            {
                _rate = _pvi.AvgTimePerFrame;
            }
            _pvi = pmt;
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

        public override int GetMediaType(ref AMMediaType pMediaType)
        {
            VideoStreamConfigCaps _caps;
            GetDefaultCaps(0, out _caps);

            int nWidth = 0;
            int nHeight = 0;

            if (CurrentMediaType.majorType == MediaType.Video)
            {
                pMediaType.Set(CurrentMediaType);
                return NOERROR;
            }

            pMediaType.majorType = DirectShow.MediaType.Video;
            pMediaType.formatType = DirectShow.FormatType.VideoInfo;

            VideoInfoHeader vih = new VideoInfoHeader();
            vih.AvgTimePerFrame = _avgTimePerFrame;
            vih.BmiHeader.Compression = BI_RGB;
            vih.BmiHeader.BitCount = (short)_bitCount;
            vih.BmiHeader.Width = nWidth;
            vih.BmiHeader.Height = nHeight;
            vih.BmiHeader.Planes = 1;
            vih.BmiHeader.ImageSize = vih.BmiHeader.Width * Math.Abs(vih.BmiHeader.Height) * vih.BmiHeader.BitCount / 8;

            if (vih.BmiHeader.BitCount == 32)
            {
                pMediaType.subType = DirectShow.MediaSubType.RGB32;
            }
            if (vih.BmiHeader.BitCount == 24)
            {
                pMediaType.subType = DirectShow.MediaSubType.RGB24;
            }
            AMMediaType.SetFormat(ref pMediaType, ref vih);
            pMediaType.fixedSizeSamples = true;
            pMediaType.sampleSize = vih.BmiHeader.ImageSize;

            return NOERROR;
        }

        public override int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties pProperties)
        {
            if (!IsConnected) return VFW_E_NOT_CONNECTED;
            AllocatorProperties _actual = new AllocatorProperties();
            HRESULT hr = (HRESULT)GetAllocatorProperties(_actual);
            if (SUCCEEDED(hr) && _actual.cBuffers <= pProperties.cBuffers && _actual.cbBuffer <= pProperties.cbBuffer && _actual.cbAlign == pProperties.cbAlign)
            {
                AllocatorProperties Actual = new AllocatorProperties();
                hr = (HRESULT)pAlloc.SetProperties(pProperties, Actual);
                if (SUCCEEDED(hr))
                {
                    pProperties.cbAlign = Actual.cbAlign;
                    pProperties.cbBuffer = Actual.cbBuffer;
                    pProperties.cbPrefix = Actual.cbPrefix;
                    pProperties.cBuffers = Actual.cBuffers;
                }
            }

            return DecideBufferSize2(ref pAlloc, ref pProperties);
        }

        public int DecideBufferSize2(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties prop)
        {
            AllocatorProperties _actual = new AllocatorProperties();

            BitmapInfoHeader _bmi = CurrentMediaType;
            prop.cbBuffer = _bmi.GetBitmapSize();

            if (prop.cbBuffer < _bmi.ImageSize)
            {
                prop.cbBuffer = _bmi.ImageSize;
            }
            if (prop.cbBuffer < m_bmi.bmiHeader.ImageSize)
            {
                prop.cbBuffer = m_bmi.bmiHeader.ImageSize;
            }

            prop.cBuffers = 1;
            prop.cbAlign = 1;
            prop.cbPrefix = 0;
            int hr = pAlloc.SetProperties(prop, _actual);
            return hr;
        }

        public override int Active()
        {
            if (_srcContext == IntPtr.Zero)
                _srcContext = USER32.GetWindowDC(IntPtr.Zero);
            if (_destContext == IntPtr.Zero)
                _destContext = GDI32.CreateCompatibleDC(_srcContext);
            m_rtStart = 0;
            lock (m_Filter.FilterLock)
            {
                m_pClock = m_Filter.Clock;
                if (m_pClock.IsValid)
                {
                    m_pClock._AddRef();
                    m_hSemaphore = new Semaphore(0, 0x7FFFFFFF);
                }
            }
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

        public unsafe int FillBuffer2(ref IMediaSampleImpl _sample)
        {
            IntPtr _ptr;
            _sample.GetPointer(out _ptr);

            //create bitmap to copy to
            IntPtr destBitmap = GDI32.CreateCompatibleBitmap(_srcContext, _captureWidth, _captureHeight);
            //select destBitmap with _destContext
            IntPtr hOld = GDI32.SelectObject(_destContext, destBitmap);
            //copy screen to context.
            GDI32.BitBlt(_destContext, 0, 0, _captureWidth, _captureHeight, _srcContext, _captureX, _captureY, GDI32.SRCCOPY/* | GDI32.CAPTUREBLT*/);
            //handle drawing cursor and click animations
            try
            {
                USER32.CURSORINFO cursorInfo;
                cursorInfo.cbSize = Marshal.SizeOf(typeof(USER32.CURSORINFO));
                if (USER32.GetCursorInfo(out cursorInfo) && cursorInfo.flags == 0x00000001 /*CURSOR_SHOWING*/)
                {
                    var iconPointer = USER32.CopyIcon(cursorInfo.hCursor);
                    USER32.ICONINFO iconInfo;
                    int iconX, iconY;
                    if (USER32.GetIconInfo(iconPointer, out iconInfo))
                    {
                        IntPtr hicon = USER32.CopyIcon(cursorInfo.hCursor);
                        Icon curIcon = Icon.FromHandle(hicon);
                        Bitmap curBitmap = curIcon.ToBitmap();
                        iconX = cursorInfo.ptScreenPos.x - ((int)iconInfo.xHotspot);
                        iconY = cursorInfo.ptScreenPos.y - ((int)iconInfo.yHotspot);

                        if (Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_LBUTTON) & 0x8000 /*KEY_PRESSED*/) ||
                            Convert.ToBoolean(USER32.GetKeyState(USER32.VirtualKeyStates.VK_RBUTTON) & 0x8000 /*KEY_PRESSED*/))
                        {
                            _lastMouseClick = DateTime.Now;
                            _lastMouseClickPosition = new Point(cursorInfo.ptScreenPos.x, cursorInfo.ptScreenPos.y);
                        }
                        using (Graphics g = Graphics.FromHdc(_destContext))
                        {
                            const int animationDuration = 500; //ms
                            const int animationMaxRadius = 25; //pixels
                            var lastClickSpan = Convert.ToInt32((DateTime.Now - _lastMouseClick).TotalMilliseconds);
                            if (lastClickSpan < animationDuration)
                            {
                                const int maxRadius = animationMaxRadius;
                                SolidBrush semiTransBrush = new SolidBrush(
                                    Color.FromArgb((int)((1 - (lastClickSpan / (double)animationDuration)) * 255), 255, 0, 0));
                                //int radius = Math.Max(5, (int)((lastClickSpan / (double)animationDuration) * maxRadius));
                                int radius = (int)((lastClickSpan / (double)animationDuration) * maxRadius);
                                var rect = new Rectangle(_lastMouseClickPosition.X - radius, _lastMouseClickPosition.Y - radius, radius * 2, radius * 2);
                                g.FillEllipse(semiTransBrush, rect);
                                semiTransBrush.Dispose();
                            }
                            g.DrawImage(curBitmap, iconX, iconY);
                            curBitmap.Dispose();
                            curIcon.Dispose();
                        }
                    }
                }
            }
            catch { /* dont want to crash if there is an error rendering the cursor*/ }

            //restore old selection (deselect destBitmap)
            GDI32.SelectObject(_destContext, hOld);
            //copy destBitmap bits to _ptr
            GDI32.GetDIBits(_destContext, destBitmap, 0, (uint)Math.Abs(_captureHeight), _ptr, ref m_bmi, 0);
            //clean up
            GDI32.DeleteObject(destBitmap);

            _sample.SetActualDataLength(_sample.GetSize());
            _sample.SetSyncPoint(true);
            return NOERROR;
        }

        public override int FillBuffer(ref IMediaSampleImpl _sample)
        {
            {
                AMMediaType pmt;
                if (S_OK == _sample.GetMediaType(out pmt))
                {
                    if (FAILED(SetMediaType(pmt)))
                    {
                        ASSERT(false);
                        _sample.SetMediaType(null);
                    }
                    pmt.Free();
                }
            }
            long _start, _stop;
            HRESULT hr = NOERROR;
            long rtLatency = _avgTimePerFrame;
            //if (FAILED(GetLatency(out rtLatency)))
            //{
            //    rtLatency = UNITS / 30;
            //}
            bool bShouldDeliver = false;
            do
            {
                if (m_dwAdviseToken == 0)
                {
                    m_pClock.GetTime(out m_rtClockStart);
                    hr = (HRESULT)m_pClock.AdvisePeriodic(m_rtClockStart + rtLatency, rtLatency, m_hSemaphore.Handle, out m_dwAdviseToken);
                    hr.Assert();
                }
                else
                {
                    if (!m_hSemaphore.WaitOne())
                    {
                        ASSERT(FALSE);
                    }
                }
                bShouldDeliver = TRUE;
                _start = m_rtStart;
                _stop = m_rtStart + 1;
                _sample.SetTime(_start, _stop);

                hr = (HRESULT)FillBuffer2(ref _sample);

                if (FAILED(hr) || S_FALSE == hr) return hr;

                m_pClock.GetTime(out m_rtClockStop);
                _sample.GetTime(out _start, out _stop);

                if (rtLatency > 0 && rtLatency * 3 < m_rtClockStop - m_rtClockStart)
                {
                    m_rtClockStop = m_rtClockStart + rtLatency;
                }
                _stop = _start + (m_rtClockStop - m_rtClockStart);
                m_rtStart = _stop;
                _sample.SetTime(_start, _stop);
                m_rtClockStart = m_rtClockStop;

                bShouldDeliver = ((_start >= 0) && (_stop >= 0));
            }
            while (!bShouldDeliver);

            return NOERROR;
        }

        #endregion

        #region IAMBufferNegotiation Members

        public int SuggestAllocatorProperties2(AllocatorProperties pprop)
        {
            AllocatorProperties _properties = new AllocatorProperties();
            HRESULT hr = (HRESULT)GetAllocatorProperties(_properties);
            if (FAILED(hr)) return hr;
            if (pprop.cbBuffer != -1 && pprop.cbBuffer < _properties.cbBuffer)
                return E_FAIL;
            if (pprop.cbAlign != -1 && pprop.cbAlign != _properties.cbAlign)
                return E_FAIL;
            if (pprop.cbPrefix != -1 && pprop.cbPrefix != _properties.cbPrefix)
                return E_FAIL;
            if (pprop.cBuffers != -1 && pprop.cBuffers < 1)
                return E_FAIL;
            return NOERROR;
        }

        public int GetAllocatorProperties2(AllocatorProperties pprop)
        {
            AMMediaType mt = CurrentMediaType;
            if (mt.majorType == MediaType.Video)
            {
                int lSize = mt.sampleSize;
                BitmapInfoHeader _bmi = mt;
                if (_bmi != null)
                {
                    if (lSize < _bmi.GetBitmapSize())
                    {
                        lSize = _bmi.GetBitmapSize();
                    }
                    if (lSize < _bmi.ImageSize)
                    {
                        lSize = _bmi.ImageSize;
                    }
                }
                pprop.cbBuffer = lSize;
                pprop.cBuffers = 1;
                pprop.cbAlign = 1;
                pprop.cbPrefix = 0;

            }
            return NOERROR;
        }

        public int SuggestAllocatorProperties(AllocatorProperties pprop)
        {
            if (IsConnected) return VFW_E_ALREADY_CONNECTED;
            HRESULT hr = (HRESULT)SuggestAllocatorProperties2(pprop);
            if (FAILED(hr))
            {
                m_pProperties = null;
                return hr;
            }
            if (m_pProperties == null)
            {
                m_pProperties = new AllocatorProperties();
                GetAllocatorProperties2(m_pProperties);
            }
            if (pprop.cbBuffer != -1) m_pProperties.cbBuffer = pprop.cbBuffer;
            if (pprop.cbAlign != -1) m_pProperties.cbAlign = pprop.cbAlign;
            if (pprop.cbPrefix != -1) m_pProperties.cbPrefix = pprop.cbPrefix;
            if (pprop.cBuffers != -1) m_pProperties.cBuffers = pprop.cBuffers;
            return NOERROR;
        }

        public int GetAllocatorProperties(AllocatorProperties pprop)
        {
            if (pprop == null) return E_POINTER;
            if (m_pProperties != null)
            {
                pprop.cbAlign = m_pProperties.cbAlign;
                pprop.cbBuffer = m_pProperties.cbBuffer;
                pprop.cbPrefix = m_pProperties.cbPrefix;
                pprop.cBuffers = m_pProperties.cBuffers;
                return NOERROR;
            }
            if (IsConnected)
            {
                HRESULT hr = (HRESULT)Allocator.GetProperties(pprop);
                if (SUCCEEDED(hr) && pprop.cBuffers > 0 && pprop.cbBuffer > 0) return hr;
            }
            return GetAllocatorProperties2(pprop);
        }

        #endregion

        #region IAMStreamConfig Members

        public int SetFormat(AMMediaType pmt)
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
                        hr = (HRESULT)SetMediaType(_newType);
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
                hr = (HRESULT)SetMediaType(_newType);
            }
            return hr;
        }

        public int GetFormat(out AMMediaType pmt)
        {
            pmt = new AMMediaType(m_mt);
            return NOERROR;
        }

        public int GetDefaultCaps(int nIndex, out VideoStreamConfigCaps _caps)
        {
            _caps = new VideoStreamConfigCaps();

            _caps.guid = FormatType.VideoInfo;
            _caps.VideoStandard = AnalogVideoStandard.None;

            _caps.InputSize.Width = _captureWidth;
            _caps.InputSize.Height = _captureHeight;

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

            const int minfps = 4;
            const int maxfps = 60;

            _caps.MinFrameInterval = UNITS / maxfps; // this is the reference INTERVAL, so the min/max is reversed
            _caps.MaxFrameInterval = UNITS / minfps;
            _caps.MinBitsPerSecond = (_caps.MinOutputSize.Width * _caps.MinOutputSize.Height * _bitCount) * minfps; //(minfps)
            _caps.MaxBitsPerSecond = (_caps.MaxOutputSize.Width * _caps.MaxOutputSize.Height * _bitCount) * maxfps; //(maxfps)

            return NOERROR;
        }

        public int GetNumberOfCapabilities(out int iCount, out int iSize)
        {
            iCount = 0;
            AMMediaType mt = new AMMediaType();
            while (GetMediaType(iCount, ref mt) == S_OK) { mt.Free(); iCount++; };
            iSize = Marshal.SizeOf(typeof(VideoStreamConfigCaps));
            return NOERROR;
        }

        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
        {
            int iCount;
            int iSize;
            HRESULT hr = (HRESULT)GetNumberOfCapabilities(out iCount, out iSize);
            if (hr.Failed) return hr;
            if (piCount != IntPtr.Zero)
            {
                Marshal.WriteInt32(piCount, iCount);
            }
            if (piSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(piSize, iSize);
            }
            return hr;
        }

        public int GetStreamCaps(int iIndex, out AMMediaType ppmt, out VideoStreamConfigCaps _caps)
        {
            ppmt = null;
            _caps = null;
            if (iIndex < 0) return E_INVALIDARG;

            ppmt = new AMMediaType();
            HRESULT hr = (HRESULT)GetMediaType(iIndex, ref ppmt);
            if (FAILED(hr)) return hr;
            if (hr == VFW_S_NO_MORE_ITEMS) return S_FALSE;
            hr = (HRESULT)GetDefaultCaps(iIndex, out _caps);
            return hr;
        }

        public int GetStreamCaps(int iIndex, IntPtr ppmt, IntPtr pSCC)
        {
            AMMediaType pmt;
            VideoStreamConfigCaps _caps;
            HRESULT hr = (HRESULT)GetStreamCaps(iIndex, out pmt, out _caps);
            if (hr != S_OK) return hr;

            if (ppmt != IntPtr.Zero)
            {
                IntPtr _ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf(pmt));
                Marshal.StructureToPtr(pmt, _ptr, true);
                Marshal.WriteIntPtr(ppmt, _ptr);
            }
            if (pSCC != IntPtr.Zero)
            {
                Marshal.StructureToPtr(_caps, pSCC, false);
            }
            return hr;
        }

        #endregion

        #region IKsPropertySet Members

        public int Set(Guid guidPropSet, int dwPropID, IntPtr pInstanceData, int cbInstanceData, IntPtr pPropData, int cbPropData)
        {
            return E_NOTIMPL;
        }

        public int Get(Guid guidPropSet, int dwPropID, IntPtr pInstanceData, int cbInstanceData, IntPtr pPropData, int cbPropData, out int pcbReturned)
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

        public int QuerySupported(Guid guidPropSet, int dwPropID, out KSPropertySupport pTypeSupport)
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

        #endregion
    }
}
