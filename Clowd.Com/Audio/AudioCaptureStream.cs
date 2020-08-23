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
//    public class AudioCaptureStream : SourceStream
//        , IAMStreamConfig
//        , IKsPropertySet
//        , IAMBufferNegotiation
//        , IAMFilterMiscFlags
//    {

//        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
//        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

//        private BaseSourceFilter m_pParent;
//        const int pBufOriginalSize = 1024 * 1024;
//        int expectedMaxBufferSize = pBufOriginalSize;
//        private long m_rtPreviousSampleEndTime;
//        private bool bVeryFirstPacket = true;
//        private bool bDiscontinuityDetected = true;

//        AlignedByteBuffer _buffer;
//        private MMDevice _captureDevice;
//        private IWavePlayer _silencePlayer;
//        private WasapiCapture _captureWave;


//        public AudioCaptureStream(string _name, BaseSourceFilter _filter)
//            : base(_name, _filter)
//        {
//            m_pParent = _filter;
//            m_mt = new AMMediaType();
//            // set the media type ...
//            GetMediaType(0, ref m_mt);
//        }

//        public override int DecideBufferSize(ref IMemAllocatorImpl pAlloc, ref AllocatorProperties prop)
//        {
//            // use our max size, or whatever they specified, if they did
//            prop.cBuffers = 1;
//            prop.cbBuffer = expectedMaxBufferSize;

//            AllocatorProperties _actual = new AllocatorProperties();
//            var hr = pAlloc.SetProperties(prop, _actual);
//            if (FAILED(hr))
//                return hr;

//            if (_actual.cbBuffer < prop.cbBuffer)
//                return E_FAIL; // this allocator is unsuitable

//            return S_OK;
//        }

//        protected override int OnThreadCreate()
//        {
//            LoopbackCaptureSetup();
//            return base.OnThreadCreate();
//        }

//        public override int Run(long tStart)
//        {
//            LoopbackCaptureClear();
//            return base.Run(tStart);
//        }

//        protected override int OnThreadDestroy()
//        {
//            LoopbackCaptureRelease();
//            return base.OnThreadDestroy();
//        }

//        private void LoopbackCaptureSetup()
//        {
//            bVeryFirstPacket = true;
//            bDiscontinuityDetected = true;
//            _buffer = new AlignedByteBuffer(expectedMaxBufferSize);
//            _captureDevice = GetMMDevice();
//            _silencePlayer = new WasapiOut(_captureDevice, AudioClientShareMode.Shared, true, 50);
//            using (var audioClient = _captureDevice.AudioClient)
//                _silencePlayer.Init(new SilenceProvider(audioClient.MixFormat));
//            _silencePlayer.Play();

//            _captureWave = new WasapiLoopbackCapture(_captureDevice);
//            _captureWave.DataAvailable += LoopbackDataRecieved;
//            _captureWave.StartRecording();
//        }

//        private void LoopbackDataRecieved(object sender, WaveInEventArgs e)
//        {
//            _buffer.Enqueue(e.Buffer, 0, e.BytesRecorded);
//        }

//        private void LoopbackCaptureClear()
//        {
//            _buffer.Clear();
//            bDiscontinuityDetected = true;
//        }

//        private void LoopbackCaptureRelease()
//        {
//            _captureWave.StopRecording();
//            _captureWave.Dispose();
//            _silencePlayer.Stop();
//            _silencePlayer.Dispose();
//            _captureDevice.Dispose();
//            _buffer.Clear();
//        }

//        private MMDevice GetMMDevice()
//        {
//            return WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
//        }

//        public override int FillBuffer(ref IMediaSampleImpl pms)
//        {
//            // allow the filter to warmup until Run is called.. so StreamTime can work right
//            FilterState myState;
//            var hr = m_pParent.GetState(int.MaxValue, out myState);
//            if (FAILED(hr))
//                return hr;

//            //while (myState != FilterState.Running)
//            //{
//            //    LoopbackCaptureClear();
//            //    Thread.Sleep(1);
//            //    Command com = default(Command);
//            //    if (CheckRequest(ref com))
//            //    {
//            //        if (com == Command.CMD_STOP)
//            //        {
//            //            // exit early
//            //            return S_FALSE;
//            //        }
//            //    }
//            //    m_pParent.GetState(int.MaxValue, out myState);
//            //}

//            if (bVeryFirstPacket)
//                LoopbackCaptureClear(); // this is to recover from pause/resume 

//            // get incoming audio data
//            IntPtr _ptr;
//            pms.GetPointer(out _ptr);
//            var _ptrSize = pms.GetSize();
//            var tmp = new byte[_ptrSize];
//            var actuallyRead = _buffer.Dequeue(tmp, 0, _ptrSize);
//            Marshal.Copy(tmp, 0, _ptr, actuallyRead);

//            hr = pms.SetActualDataLength(actuallyRead);
//            if (FAILED(hr))
//                return hr;

//            // now lets set start/end time stamps
//            WaveFormatEx pwfexCurrent = (WaveFormatEx)Marshal.PtrToStructure(m_mt.formatPtr, typeof(WaveFormatEx));

//            /// nano seconds / 100 (see reftime.h)
//            long sampleTimeUsed = (UNITS * actuallyRead) / pwfexCurrent.nAvgBytesPerSec;
//            long rtStart;
//            if (bDiscontinuityDetected || bVeryFirstPacket)
//            {
//                m_pParent.StreamTime(out rtStart);
//                if (bVeryFirstPacket)
//                {
//                    // my theory is that sometimes the first packet is "big" because it takes a while for ffmpeg
//                    // to start up. so instruct it to think this frame is slightly in the past.
//                    rtStart -= sampleTimeUsed;
//                }
//                else if (bDiscontinuityDetected)
//                {
//                    // same deal here.. as if i knew what i was doing.. LOL
//                    rtStart = Math.Max(m_rtPreviousSampleEndTime, rtStart - sampleTimeUsed);
//                }
//            }
//            else
//            {
//                // since there has not been discontinuity, we should be safe
//                // to tell it this packet starts where the previous ended
//                rtStart = m_rtPreviousSampleEndTime;
//            }

//            m_rtPreviousSampleEndTime = rtStart + sampleTimeUsed;

//            // attempt to disallow drift / keep these in sync
//            long now;
//            m_pParent.StreamTime(out now);
//            var diff = now - m_rtPreviousSampleEndTime;
//            if (diff > 0)
//                m_rtPreviousSampleEndTime += 1;
//            else if (diff < -100000)
//                m_rtPreviousSampleEndTime -= 1;

//            // note that this can set it to negative start time which is expected when a graph is starting up
//            hr = pms.SetTime(rtStart, m_rtPreviousSampleEndTime);
//            if (FAILED(hr)) return hr;

//            // tell this it's not a preroll.. so to actually use the sample
//            pms.SetPreroll(false);

//            pms.SetMediaType(null);

//            pms.SetDiscontinuity(bDiscontinuityDetected || bVeryFirstPacket);

//            hr = pms.SetSyncPoint(true); // true on every sample for PCM audio
//            if (FAILED(hr)) return hr;

//            bDiscontinuityDetected = false;
//            bVeryFirstPacket = false;

//            return S_OK;
//        }

//        public override int GetMediaType(int iPosition, ref AMMediaType pMediaType)
//        {
//            if (iPosition == 0)
//                return GetMediaType(ref pMediaType);

//            return VFW_S_NO_MORE_ITEMS;
//        }

//        public override int GetMediaType(ref AMMediaType pMediaType)
//        {
//            var pwfex = new WaveFormatEx();
//            setupPwfex(ref pwfex, ref pMediaType, true);
//            return S_OK;
//        }

//        private int setupPwfex(ref WaveFormatEx pwfex, ref AMMediaType pmt, bool setFormat = false)
//        {
//            const int BITS_PER_BYTE = 8;
//            using (var pMMDevice = GetMMDevice())
//            using (var pAudioClient = pMMDevice.AudioClient)
//            {
//                var pwfx = pAudioClient.MixFormat;

//                pwfex.wFormatTag = (ushort)pwfx.Encoding;
//                pwfex.cbSize = (ushort)pwfx.ExtraSize;
//                pwfex.nChannels = (ushort)pwfx.Channels;
//                pwfex.nSamplesPerSec = pwfx.SampleRate;
//                pwfex.wBitsPerSample = (ushort)pwfx.BitsPerSample;
//                pwfex.nBlockAlign = (ushort)((pwfex.wBitsPerSample * pwfex.nChannels) / BITS_PER_BYTE);
//                pwfex.nAvgBytesPerSec = pwfex.nSamplesPerSec * pwfex.nBlockAlign;

//                if (pmt == null)
//                    pmt = new AMMediaType();

//                // https://www.cs.vu.nl/~eliens/media/hush-src-multi-BaseClasses-mtype.cpp
//                pmt.majorType = MediaType.Audio;

//                if (pwfex.wFormatTag == (ushort)WaveFormatEncoding.Extensible)
//                    pmt.subType = ((NAudio.Wave.WaveFormatExtensible)pwfx).SubFormat;
//                else
//                    pmt.subType = new FOURCC(pwfex.wFormatTag);

//                pmt.formatType = FormatType.WaveEx;
//                pmt.fixedSizeSamples = true;
//                pmt.temporalCompression = false;
//                pmt.sampleSize = pwfex.nBlockAlign;
//                pmt.unkPtr = IntPtr.Zero;
//                if (setFormat)
//                {
//                    pmt.SetFormat(pwfex);
//                }
//                return S_OK;
//            }
//        }

//        public int SetFormat([In, MarshalAs(UnmanagedType.LPStruct)] AMMediaType pmt)
//        {
//            // this method means you "must" use this type from now un, unless it's null, then it means "reset"
//            if (pmt == null)
//                return S_OK; // we reset... yeah.. sure we did.. (not).. todo?

//            if (CheckMediaType(pmt) != S_OK)
//                return E_FAIL; // just in case :P

//            m_mt = pmt;

//            ConnectedTo(out var pin);
//            if (pin != null)
//            {
//                var pGraph = m_pParent.FilterGraph;
//                pGraph.Reconnect(this);
//            }

//            return S_OK;
//        }

//        public int GetFormat([Out] out AMMediaType pmt)
//        {
//            pmt = new AMMediaType(m_mt);
//            return S_OK;
//        }

//        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
//        {
//            Marshal.WriteInt32(piCount, 1);
//            Marshal.WriteInt32(piSize, Marshal.SizeOf(typeof(AudioStreamConfigCaps)));
//            return S_OK;
//        }

//        public int GetStreamCaps([In] int iIndex, [In, Out] IntPtr ppmt, [In] IntPtr pSCC)
//        {
//            if (iIndex < 0) return E_INVALIDARG;
//            if (iIndex > 0) return S_FALSE;
//            if (pSCC == IntPtr.Zero) return E_POINTER;

//            var ppMediaType = new AMMediaType(m_mt);
//            var pAudioFormat = new WaveFormatEx();
//            setupPwfex(ref pAudioFormat, ref ppMediaType);

//            var capabilities = new AudioStreamConfigCaps();
//            capabilities.guid = MediaType.Audio;
//            capabilities.MaximumChannels = pAudioFormat.nChannels;
//            capabilities.MinimumChannels = pAudioFormat.nChannels;
//            capabilities.ChannelsGranularity = 1;
//            capabilities.MinimumSampleFrequency = (uint)pAudioFormat.nSamplesPerSec;
//            capabilities.MaximumSampleFrequency = (uint)pAudioFormat.nSamplesPerSec;
//            capabilities.SampleFrequencyGranularity = 11025;
//            capabilities.MaximumBitsPerSample = pAudioFormat.wBitsPerSample;
//            capabilities.MinimumBitsPerSample = pAudioFormat.wBitsPerSample;
//            capabilities.BitsPerSampleGranularity = 16;

//            if (ppmt != IntPtr.Zero)
//            {
//                IntPtr _ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf(ppMediaType));
//                Marshal.StructureToPtr(ppMediaType, _ptr, true);
//                Marshal.WriteIntPtr(ppmt, _ptr);
//            }

//            if (pSCC != IntPtr.Zero)
//            {
//                Marshal.StructureToPtr(capabilities, pSCC, false);
//            }

//            return S_OK;
//        }

//        public int Set([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In] IntPtr pPropData, [In] int cbPropData)
//        {
//            return E_NOTIMPL;
//        }

//        public int Get([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [In] IntPtr pInstanceData, [In] int cbInstanceData, [In, Out] IntPtr pPropData, [In] int cbPropData, [Out] out int pcbReturned)
//        {
//            pcbReturned = Marshal.SizeOf(typeof(Guid));
//            if (guidPropSet != PropSetID.Pin)
//            {
//                return E_PROP_SET_UNSUPPORTED;
//            }
//            if (dwPropID != (int)AMPropertyPin.Category)
//            {
//                return E_PROP_ID_UNSUPPORTED;
//            }
//            if (pPropData == IntPtr.Zero)
//            {
//                return NOERROR;
//            }
//            if (cbPropData < Marshal.SizeOf(typeof(Guid)))
//            {
//                return E_UNEXPECTED;
//            }
//            Marshal.StructureToPtr(PinCategory.Capture, pPropData, false);
//            return NOERROR;
//        }

//        public int QuerySupported([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidPropSet, [In] int dwPropID, [Out] out KSPropertySupport pTypeSupport)
//        {
//            // we support getting this property, but not setting it
//            pTypeSupport = KSPropertySupport.Get;
//            if (guidPropSet != PropSetID.Pin) return E_PROP_SET_UNSUPPORTED;
//            if (dwPropID != (int)AMPropertyPin.Category) return E_PROP_ID_UNSUPPORTED;
//            return S_OK;
//        }

//        public int SuggestAllocatorProperties([In] AllocatorProperties pprop)
//        {
//            // maybe we shouldn't even care... seriously, why let them make it smaller
//            // TODO test it both ways with FME, fast computer / slow computer does it make a difference?

//            AllocatorProperties _properties = new AllocatorProperties();

//            int requested = pprop.cbBuffer;
//            if (pprop.cBuffers > 0)
//                requested *= pprop.cBuffers;
//            if (pprop.cbPrefix > 0)
//                requested += pprop.cbPrefix;

//            if (requested <= pBufOriginalSize)
//            {
//                expectedMaxBufferSize = requested;
//                // they requested it? you're just requesting possible problems..
//                // oh well! you requested it!
//                return S_OK;
//            }
//            else
//            {
//                return E_FAIL;
//            }
//        }

//        public int GetAllocatorProperties([Out] AllocatorProperties pprop)
//        {
//            return E_FAIL; // this is never called
//        }

//        public int GetMiscFlags()
//        {
//            return (int)AMFilterMiscFlags.IsSource;
//        }
//    }
//}
