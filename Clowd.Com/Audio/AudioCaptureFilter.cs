//using DirectShow;
//using DirectShow.BaseClasses;
//using NAudio.CoreAudioApi;
//using NAudio.Wave;
//using Sonic;
//using System;
//using System.Collections.Generic;
//using System.Drawing;
//using System.Drawing.Drawing2D;
//using System.IO;
//using System.Runtime.InteropServices;
//using System.Text;
//using System.Threading;
//using System.Windows.Forms;

//namespace Clowd.Com
//{
//    [ComVisible(false)]
//    [Guid("98957b9c-71c2-46b7-95a9-eec80d9317e7")]
//    [AMovieSetup(Merit.Normal, AMovieSetup.CLSID_AudioInputDeviceCategory)]
//    public class AudioCaptureFilter : BaseSourceFilter, IAMStreamConfig, IKsPropertySet, IAMFilterMiscFlags
//    {
//        public const string FRIENDLY_NAME = "clowd-audio";

//        private AudioCaptureStream _stream;

//        public AudioCaptureFilter() : base(FRIENDLY_NAME)
//        {
//            _stream = new AudioCaptureStream("CaptureAudioPin", this);
//            AddPin(_stream);
//        }

//        protected override int OnInitializePins()
//        {
//            return NOERROR;
//        }

//        public override int Pause()
//        {
//            return base.Pause();
//        }

//        public override int Stop()
//        {
//            return base.Stop();
//        }

//        protected override HRESULT WriteToStream(Stream _stream)
//        {
//            return base.WriteToStream(_stream);
//        }

//        protected override HRESULT ReadFromStream(Stream _stream)
//        {
//            return base.ReadFromStream(_stream);
//        }

//        public int SetFormat([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt)
//        {
//            return _stream.SetFormat(pmt);
//        }

//        public int GetFormat([Out] out AMMediaType pmt)
//        {
//            return _stream.GetFormat(out pmt);
//        }

//        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
//        {
//            return _stream.GetNumberOfCapabilities(piCount, piSize);
//        }

//        public int GetStreamCaps([In] int iIndex, [In, Out] IntPtr ppmt, [In] IntPtr pSCC)
//        {
//            return _stream.GetStreamCaps(iIndex, ppmt, pSCC);
//        }

//        public int Set([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In] IntPtr pPropData, [In] int cbPropData)
//        {
//            return _stream.Set(guidPropSet, dwPropID, pInstanceData, cbInstanceData, pPropData, cbPropData);
//        }

//        public int Get([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In, Out] IntPtr pPropData, [In] int cbPropData, [Out] out int pcbReturned)
//        {
//            return _stream.Get(guidPropSet, dwPropID, pInstanceData, cbInstanceData, pPropData, cbPropData, out pcbReturned);
//        }

//        public int QuerySupported([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [Out] out KSPropertySupport pTypeSupport)
//        {
//            return _stream.QuerySupported(guidPropSet, dwPropID, out pTypeSupport);
//        }

//        public int GetMiscFlags()
//        {
//            return _stream.GetMiscFlags();
//        }
//    }
//}
