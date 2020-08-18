using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Clowd.Com
{
    [ComVisible(true)]
    [Guid("47e87240-d32c-48cb-a756-e9a006423a8c")]
    [AMovieSetup(Merit.Normal, AMovieSetup.CLSID_VideoInputDeviceCategory)]
    public class VideoCaptureFilter : BaseSourceFilter, IAMFilterMiscFlags
    {
        public const string FRIENDLY_NAME = "clowd-video";

        protected int _bitCount = 32;
        protected int _captureWidth = 0;
        protected int _captureHeight = 0;
        protected int _captureX = 0;
        protected int _captureY = 0;
        protected long _avgTimePerFrame = UNITS / 30;
        private bool _dpiAware = false;
        private GDI32.BitmapInfo m_bmi = new GDI32.BitmapInfo();
        private DateTime _lastMouseClick = DateTime.Now.AddSeconds(-5);
        private Point _lastMouseClickPosition = new Point(0, 0);
        IntPtr _srcContext = IntPtr.Zero;
        IntPtr _destContext = IntPtr.Zero;

        public VideoCaptureFilter() : base(FRIENDLY_NAME)
        {
            m_bmi.bmiHeader = new BitmapInfoHeader();
            AddPin(new SourceFilterStream("CaptureVideoPin", this));
        }

        public int GetMiscFlags()
        {
            return (int)AMFilterMiscFlags.IsSource;
        }

        protected override int OnInitializePins()
        {
            return NOERROR;
        }

        public override int Pause()
        {
            if (m_State == FilterState.Stopped)
            {
                //init here
                if (_srcContext == IntPtr.Zero)
                    _srcContext = USER32.GetWindowDC(IntPtr.Zero);
                if (_destContext == IntPtr.Zero)
                    _destContext = GDI32.CreateCompatibleDC(_srcContext);
            }
            return base.Pause();
        }

        public override int Stop()
        {
            int hr = base.Stop();
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
            return hr;
        }

        public int CheckMediaType(AMMediaType pmt)
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

        public int SetMediaType(AMMediaType pmt)
        {
            lock (m_Lock)
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

        public int GetMediaType(int iPosition, ref AMMediaType pMediaType)
        {
            if (iPosition < 0) return E_INVALIDARG;
            VideoStreamConfigCaps _caps;
            GetDefaultCaps(0, out _caps);

            int nWidth = 0;
            int nHeight = 0;

            if (iPosition == 0)
            {
                if (Pins.Count > 0 && Pins[0].CurrentMediaType.majorType == MediaType.Video)
                {
                    pMediaType.Set(Pins[0].CurrentMediaType);
                    return NOERROR;
                }
                nWidth = _caps.InputSize.Width;
                nHeight = _caps.InputSize.Height;
            }
            else
            {
                iPosition--;
                nWidth = _caps.MinOutputSize.Width + _caps.OutputGranularityX * iPosition;
                nHeight = _caps.MinOutputSize.Height + _caps.OutputGranularityY * iPosition;
                if (nWidth > _caps.MaxOutputSize.Width || nHeight > _caps.MaxOutputSize.Height)
                {
                    return VFW_S_NO_MORE_ITEMS;
                }
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

        public int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties prop)
        {
            AllocatorProperties _actual = new AllocatorProperties();

            BitmapInfoHeader _bmi = (BitmapInfoHeader)Pins[0].CurrentMediaType;
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

        public unsafe int FillBuffer(ref IMediaSampleImpl _sample)
        {
            IntPtr _ptr;
            _sample.GetPointer(out _ptr);

            //create bitmap to copy to
            IntPtr destBitmap = GDI32.CreateCompatibleBitmap(_srcContext, _captureWidth, _captureHeight);
            //select destBitmap with _destContext
            IntPtr hOld = GDI32.SelectObject(_destContext, destBitmap);
            //copy screen to context.
            GDI32.BitBlt(_destContext, 0, 0, _captureWidth, _captureHeight, _srcContext, _captureX, _captureY, GDI32.TernaryRasterOperations.SRCCOPY/* | GDI32.CAPTUREBLT*/);
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

        public int GetLatency(out long prtLatency)
        {
            prtLatency = UNITS / 30;
            AMMediaType mt = Pins[0].CurrentMediaType;
            if (mt.majorType == MediaType.Video)
            {
                VideoInfoHeader _pvi = mt;
                if (_pvi != null)
                {
                    prtLatency = _pvi.AvgTimePerFrame;
                }
                _pvi = mt;
                if (_pvi != null)
                {
                    prtLatency = _pvi.AvgTimePerFrame;
                }
            }
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

        public int SuggestAllocatorProperties(AllocatorProperties pprop)
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

        public int GetAllocatorProperties(AllocatorProperties pprop)
        {
            AMMediaType mt = Pins[0].CurrentMediaType;
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

        public int GetDefaultCaps(int nIndex, out VideoStreamConfigCaps _caps)
        {
            _caps = new VideoStreamConfigCaps();

            _caps.guid = FormatType.VideoInfo;
            _caps.VideoStandard = AnalogVideoStandard.None;

            _caps.InputSize.Width = _captureWidth;
            _caps.InputSize.Height = _captureHeight;

            _caps.MinCroppingSize.Width = 320;
            _caps.MinCroppingSize.Height = 240;
            if (!_dpiAware)
            {
                //needs to be called before accessing screen sizes so the correct pixel value is returned.
                USER32.SetProcessDPIAware();
                _dpiAware = true;
            }
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
            _caps.MinFrameInterval = UNITS / 60;
            _caps.MaxFrameInterval = UNITS / 15;
            _caps.MinBitsPerSecond = (_caps.MinOutputSize.Width * _caps.MinOutputSize.Height * _bitCount) * 15; //(minfps)
            _caps.MaxBitsPerSecond = (_caps.MaxOutputSize.Width * _caps.MaxOutputSize.Height * _bitCount) * 60; //(maxfps)

            return NOERROR;
        }
    }
}
