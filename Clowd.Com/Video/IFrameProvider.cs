using DirectShow;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace Clowd.Com.Video
{
    interface IFrameProvider : IDisposable
    {
        int SetCaptureProperties(CaptureProperties properties);
        int GetCaptureProperties(out CaptureProperties properties);
        int CopyScreenToSamplePtr(ref IMediaSampleImpl _sample);
    }
}
