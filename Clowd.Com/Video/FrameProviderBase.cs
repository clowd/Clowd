using DirectShow;
using Sonic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Clowd.Com.Video
{
    abstract class FrameProviderBase : IFrameProvider
    {
        protected CaptureProperties _properties = new CaptureProperties() { BitCount = 32 };

        public abstract int CopyScreenToSamplePtr(ref IMediaSampleImpl _sample);

        public abstract void Dispose();

        public virtual int SetCaptureProperties(CaptureProperties properties)
        {
            _properties = properties.Clone();
            return COMHelper.S_OK;
        }

        public virtual int GetCaptureProperties(out CaptureProperties properties)
        {
            properties = _properties.Clone();
            return COMHelper.S_OK;
        }
    }
}
