using DirectShow;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace Clowd.Com.Video
{
    interface IFrameProvider
    {
        int CopyScreenToSamplePtr(Rectangle captureArea, ref IMediaSampleImpl _sample);
    }
}
