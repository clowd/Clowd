using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DShowVideoFilter
{
    [ComVisible(false)]
    public class SourceFilterStream : SourceStream
                    , IAMStreamControl
                    , IKsPropertySet
                    , IAMPushSource
                    , IAMLatency
                    , IAMStreamConfig
                    , IAMBufferNegotiation
    {

        #region Constants

        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

        #endregion

        #region Variables

        protected object m_csPinLock = new object();
        protected object m_csTimeLock = new object();
        protected long m_rtStart = 0;
        protected long m_rtStreamOffset = 0;
        protected long m_rtStreamOffsetMax = -1;
        protected long m_rtStartAt = -1;
        protected long m_rtStopAt = -1;
        protected int m_dwStopCookie = 0;
        protected int m_dwStartCookie = 0;
        protected bool m_bShouldFlush = false;
        protected bool m_bStartNotified = false;
        protected bool m_bStopNotified = false;
        protected AllocatorProperties m_pProperties = null;
        protected IReferenceClockImpl m_pClock = null;
        // Clock Token
        protected int m_dwAdviseToken = 0;
        // Clock Semaphore
        protected Semaphore m_hSemaphore = null;
        // Clock Start time
        protected long m_rtClockStart = 0;
        // Clock Stop time
        protected long m_rtClockStop = 0;

        #endregion

        #region Constructor

        public SourceFilterStream(string _name, BaseSourceFilter _filter)
            : base(_name, _filter)
        {
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
            return (m_Filter as ScreenCaptureFilter).SetMediaType(mt);
        }

        public override int CheckMediaType(AMMediaType pmt)
        {
            return (m_Filter as ScreenCaptureFilter).CheckMediaType(pmt);
        }

        public override int GetMediaType(int iPosition, ref AMMediaType pMediaType)
        {
            return (m_Filter as ScreenCaptureFilter).GetMediaType(iPosition, ref pMediaType);
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
            return (m_Filter as ScreenCaptureFilter).DecideBufferSize(ref pAlloc, ref pProperties);
        }

        public override int Active()
        {
            m_rtStart = 0;
            m_bStartNotified = false;
            m_bStopNotified = false;
            {
                lock (m_Filter.FilterLock)
                {
                    m_pClock = m_Filter.Clock;
                    if (m_pClock.IsValid)
                    {
                        m_pClock._AddRef();
                        m_hSemaphore = new Semaphore(0, 0x7FFFFFFF);
                    }
                }
            }
            return base.Active();
        }

        public override int Inactive()
        {
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
            long rtLatency;
            if (FAILED(GetLatency(out rtLatency)))
            {
                rtLatency = UNITS / 30;
            }
            bool bShouldDeliver = false;
            do
            {
                if (m_dwAdviseToken == 0)
                {
                    m_pClock.GetTime(out m_rtClockStart);
#pragma warning disable 0612, 0618
                    hr = (HRESULT)m_pClock.AdvisePeriodic(m_rtClockStart + rtLatency, rtLatency, m_hSemaphore.Handle, out m_dwAdviseToken);
#pragma warning restore 0612, 0618
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
                hr = (HRESULT)(m_Filter as ScreenCaptureFilter).FillBuffer(ref _sample);
                if (FAILED(hr) || S_FALSE == hr) return hr;

                m_pClock.GetTime(out m_rtClockStop);
                _sample.GetTime(out _start, out _stop);

                if (rtLatency > 0 && rtLatency * 3 < m_rtClockStop - m_rtClockStart)
                {
                    m_rtClockStop = m_rtClockStart + rtLatency;
                }
                _stop = _start + (m_rtClockStop - m_rtClockStart);
                m_rtStart = _stop;
                lock (m_csPinLock)
                {
                    _start -= m_rtStreamOffset;
                    _stop -= m_rtStreamOffset;
                }
                _sample.SetTime(_start, _stop);
                m_rtClockStart = m_rtClockStop;

                bShouldDeliver = ((_start >= 0) && (_stop >= 0));

                if (bShouldDeliver)
                {
                    lock (m_csPinLock)
                        if (m_rtStartAt != -1)
                        {
                            if (m_rtStartAt > _start)
                            {
                                bShouldDeliver = FALSE;
                            }
                            else
                            {
                                if (m_dwStartCookie != 0 && !m_bStartNotified)
                                {
                                    m_bStartNotified = TRUE;
                                    hr = (HRESULT)m_Filter.NotifyEvent(EventCode.StreamControlStarted, Marshal.GetIUnknownForObject(this), (IntPtr)m_dwStartCookie);
                                    if (FAILED(hr)) return hr;
                                }
                            }
                        }
                    if (!bShouldDeliver) continue;
                    if (m_rtStopAt != -1)
                    {
                        if (m_rtStopAt < _start)
                        {
                            if (!m_bStopNotified)
                            {
                                m_bStopNotified = TRUE;
                                if (m_dwStopCookie != 0)
                                {
                                    hr = (HRESULT)m_Filter.NotifyEvent(EventCode.StreamControlStopped, Marshal.GetIUnknownForObject(this), (IntPtr)m_dwStopCookie);
                                    if (FAILED(hr)) return hr;
                                }
                                bShouldDeliver = m_bShouldFlush;
                            }
                            else
                            {
                                bShouldDeliver = FALSE;
                            }
                            // EOS
                            if (!bShouldDeliver) return S_FALSE;
                        }
                    }
                }
            }
            while (!bShouldDeliver);

            return NOERROR;
        }

        #endregion

        #region IAMBufferNegotiation Members

        public int SuggestAllocatorProperties(AllocatorProperties pprop)
        {
            if (IsConnected) return VFW_E_ALREADY_CONNECTED;
            HRESULT hr = (HRESULT)(m_Filter as ScreenCaptureFilter).SuggestAllocatorProperties(pprop);
            if (FAILED(hr))
            {
                m_pProperties = null;
                return hr;
            }
            if (m_pProperties == null)
            {
                m_pProperties = new AllocatorProperties();
                (m_Filter as ScreenCaptureFilter).GetAllocatorProperties(m_pProperties);
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
            return (m_Filter as ScreenCaptureFilter).GetAllocatorProperties(pprop);
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
                        hr = (HRESULT)(m_Filter as ScreenCaptureFilter).SetMediaType(_newType);
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
                hr = (HRESULT)(m_Filter as ScreenCaptureFilter).SetMediaType(_newType);
            }
            return hr;
        }

        public int GetFormat(out AMMediaType pmt)
        {
            pmt = new AMMediaType(m_mt);
            return NOERROR;
        }

        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
        {
            int iCount;
            int iSize;
            HRESULT hr = (HRESULT)(m_Filter as ScreenCaptureFilter).GetNumberOfCapabilities(out iCount, out iSize);
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

        public int GetStreamCaps(int iIndex, IntPtr ppmt, IntPtr pSCC)
        {
            AMMediaType pmt;
            VideoStreamConfigCaps _caps;
            HRESULT hr = (HRESULT)(m_Filter as ScreenCaptureFilter).GetStreamCaps(iIndex, out pmt, out _caps);
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

        #region IAMPushSource Members

        public int GetPushSourceFlags(out AMPushSourceFlags pFlags)
        {
            pFlags = AMPushSourceFlags.None;
            return NOERROR;
        }

        public int SetPushSourceFlags(AMPushSourceFlags Flags)
        {
            return E_NOTIMPL;
        }

        public int SetStreamOffset(long rtOffset)
        {
            lock (m_csPinLock)
            {
                m_rtStreamOffset = rtOffset;
                if (m_rtStreamOffset > m_rtStreamOffsetMax) m_rtStreamOffsetMax = m_rtStreamOffset;
            }
            return NOERROR;
        }

        public int GetStreamOffset(out long prtOffset)
        {
            prtOffset = m_rtStreamOffset;
            return NOERROR;
        }

        public int GetMaxStreamOffset(out long prtMaxOffset)
        {
            prtMaxOffset = 0;
            if (m_rtStreamOffsetMax == -1)
            {
                HRESULT hr = (HRESULT)GetLatency(out m_rtStreamOffsetMax);
                if (FAILED(hr)) return hr;
                if (m_rtStreamOffsetMax < m_rtStreamOffset) m_rtStreamOffsetMax = m_rtStreamOffset;
            }
            prtMaxOffset = m_rtStreamOffsetMax;
            return NOERROR;
        }

        public int SetMaxStreamOffset(long rtMaxOffset)
        {
            if (rtMaxOffset < m_rtStreamOffset) return E_INVALIDARG;
            m_rtStreamOffsetMax = rtMaxOffset;
            return NOERROR;
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

        #region IAMStreamControl Members

        public int StartAt(DsLong ptStart, int dwCookie)
        {
            lock (m_csPinLock)
            {
                if (ptStart != null && ptStart != MAX_LONG)
                {
                    m_rtStartAt = ptStart;
                    m_dwStartCookie = dwCookie;
                }
                else
                {
                    m_rtStartAt = -1;
                    m_dwStartCookie = 0;
                }
            }
            return NOERROR;
        }

        public int StopAt(DsLong ptStop, bool bSendExtra, int dwCookie)
        {
            lock (m_csPinLock)
            {
                if (ptStop != null && ptStop != MAX_LONG)
                {
                    m_rtStopAt = ptStop;
                    m_bShouldFlush = bSendExtra;
                    m_dwStopCookie = dwCookie;
                }
                else
                {
                    m_rtStopAt = -1;
                    m_bShouldFlush = false;
                    m_dwStopCookie = 0;
                }
            }
            return NOERROR;
        }

        public int GetInfo(out AMStreamInfo pInfo)
        {
            lock (m_csPinLock)
            {
                pInfo = new AMStreamInfo();
                pInfo.dwFlags = AMStreamInfoFlags.None;

                if (m_rtStart < m_rtStartAt)
                {
                    pInfo.dwFlags = pInfo.dwFlags | AMStreamInfoFlags.Discarding;
                }
                if (m_rtStartAt != -1)
                {
                    pInfo.dwFlags = pInfo.dwFlags | AMStreamInfoFlags.StartDefined;
                    pInfo.tStart = m_rtStartAt;
                    pInfo.dwStartCookie = m_dwStartCookie;
                }
                if (m_rtStopAt != -1)
                {
                    pInfo.dwFlags = pInfo.dwFlags | AMStreamInfoFlags.StopDefined;
                    pInfo.tStop = m_rtStopAt;
                    pInfo.dwStopCookie = m_dwStopCookie;
                }
                if (m_bShouldFlush) pInfo.dwFlags = pInfo.dwFlags | AMStreamInfoFlags.StopSendExtra;
            }
            return NOERROR;
        }

        #endregion

        #region IAMLatency Members

        public int GetLatency(out long prtLatency)
        {
            return (m_Filter as ScreenCaptureFilter).GetLatency(out prtLatency);
        }

        #endregion
    }
}
