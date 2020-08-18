using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Clowd.Com.Video
{
    [ComVisible(false)]
    public abstract class CSynchronizedSourceStream : SourceStream, IAMLatency
    {
        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

        private IReferenceClockImpl _clock = null;
        private Semaphore _semaphore = null;
        private int _dwAdviseToken = 0;
        private long _rtClockStart = 0;
        private long _avgTimePerFrame;

        public CSynchronizedSourceStream(string name, long defaultLatency, BaseSourceFilter filter) : base(name, filter)
        {
            _avgTimePerFrame = defaultLatency;
        }

        public override int Active()
        {
            lock (m_Filter.FilterLock)
            {
                _clock = m_Filter.Clock;
                if (_clock.IsValid)
                {
                    _clock._AddRef();
                    _semaphore = new Semaphore(0, 0x7FFFFFFF);
                }
            }
            return base.Active();
        }

        public override int Inactive()
        {
            var hr = base.Inactive();
            if (_clock != null)
            {
                if (_dwAdviseToken != 0)
                {
                    _clock.Unadvise(_dwAdviseToken);
                    _dwAdviseToken = 0;
                }
                _clock._Release();
                _clock = null;
                if (_semaphore != null)
                {
                    _semaphore.Close();
                    _semaphore = null;
                }
            }
            return hr;
        }


        protected int WaitFrameStart(out long frameStart)
        {
            int hr;
            long rtLatency = _avgTimePerFrame;

            if (_dwAdviseToken == 0)
            {
                // set up clock advise. this will signal the semaphore every 'rtLatency'
                _clock.GetTime(out _rtClockStart);
#pragma warning disable CS0618 // Type or member is obsolete
                hr = _clock.AdvisePeriodic(_rtClockStart + rtLatency, rtLatency, _semaphore.Handle, out _dwAdviseToken);
#pragma warning restore CS0618 // Type or member is obsolete
                ASSERT(SUCCEEDED(hr));
            }
            else
            {
                hr = _semaphore.WaitOne() ? S_OK : E_FAIL;
                ASSERT(SUCCEEDED(hr));
            }

            frameStart = _rtClockStart;

            return hr;
        }

        protected int MarkFrameEnd(out long frameEnd)
        {
            int hr = _clock.GetTime(out frameEnd);

            // some basic end time correction if we are drifting
            //if (_avgTimePerFrame > 0 && _avgTimePerFrame * 3 < frameEnd - _rtClockStart)
            //    frameEnd = _rtClockStart + _avgTimePerFrame;

            _rtClockStart = frameEnd; // next frame starts where this one ends
            return hr;
        }

        protected int SetLatency(long refLatency)
        {
            // can not update latency while filter is running / AdvisePeriodic is set
            if (_dwAdviseToken != 0)
                return E_FAIL;

            _avgTimePerFrame = refLatency;
            return S_OK;
        }

        public int GetLatency(out long prtLatency)
        {
            prtLatency = _avgTimePerFrame;
            return S_OK;
        }
    }
}
