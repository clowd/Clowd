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
    [Guid(GUID)]
    [AMovieSetup(Merit.DoNotUse, AMovieSetup.CLSID_VideoInputDeviceCategory)]
    public class CVideoSource : BaseSourceFilter, IAMFilterMiscFlags
    {
        public const string GUID = "5294db6e-a762-44e1-bb6a-7279295e923c";
        public const string NAME = "clowdvcap1";
        public const string PIN_NAME = NAME + " output pin";

        public CVideoSource() : base(NAME)
        {
            AddPin(new CVideoPin(PIN_NAME, this));
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
}
