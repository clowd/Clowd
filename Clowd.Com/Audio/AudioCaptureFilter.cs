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
    [Guid("98957b9c-71c2-46b7-95a9-eec80d9317e7")]
    [AMovieSetup(Merit.Normal, AMovieSetup.CLSID_AudioInputDeviceCategory)]
    class AudioCaptureFilter : BaseSourceFilter, IAMFilterMiscFlags
    {
        public const string FRIENDLY_NAME = "clowd-audio";
        public AudioCaptureFilter() : base(FRIENDLY_NAME)
        {
            AddPin(new AudioCaptureStream("Capture", this));

        }
        protected override int OnInitializePins()
        {
            return NOERROR;
        }

        public override int Pause()
        {
            return base.Pause();
        }

        public override int Stop()
        {
            return base.Stop();
        }

        public override int GetState(int dwMilliSecsTimeout, out FilterState filtState)
        {
            return base.GetState(dwMilliSecsTimeout, out filtState);
        }

        [ComVisible(false)]
        public class AudioCaptureStream : SourceStream
        {
            public AudioCaptureStream(string _name, BaseSourceFilter _filter)
                : base(_name, _filter)
            {

            }

            public override int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties prop)
            {
                throw new NotImplementedException();
            }

            public override int FillBuffer(ref IMediaSampleImpl _sample)
            {
                throw new NotImplementedException();
            }
        }

        public int GetMiscFlags()
        {
            return (int)AMFilterMiscFlags.IsSource;
        }
    }
}
