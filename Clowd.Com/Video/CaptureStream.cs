using DirectShow;
using DirectShow.BaseClasses;
using Sonic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Clowd.Com.Video
{
    public abstract class CaptureStream : SourceStream, IKsPropertySet, IAMStreamConfig
    {
        public static HRESULT E_PROP_SET_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070492; } } }
        public static HRESULT E_PROP_ID_UNSUPPORTED { get { unchecked { return (HRESULT)0x80070490; } } }

        public CaptureStream(string name, BaseSourceFilter filter) : base(name, filter)
        {

        }

        public int Set(Guid guidPropSet, int dwPropID, IntPtr pInstanceData, int cbInstanceData, IntPtr pPropData, int cbPropData)
        {
            return E_NOTIMPL;
        }

        public int Get(Guid guidPropSet, int dwPropID, IntPtr pInstanceData, int cbInstanceData, IntPtr pPropData, int cbPropData, out int pcbReturned)
        {
            // we only support getting the Pin Category
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
            // we only support getting the Pin Category
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

        public int GetNumberOfCapabilities(IntPtr piCount, IntPtr piSize)
        {
            int iCount = 0, iSize = Marshal.SizeOf(typeof(VideoStreamConfigCaps));

            AMMediaType mt = new AMMediaType();
            while (GetMediaType(iCount, ref mt) == S_OK)
            {
                mt.Free();
                iCount++;
            };

            if (piCount != IntPtr.Zero)
            {
                Marshal.WriteInt32(piCount, iCount);
            }

            if (piSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(piSize, iSize);
            }

            return S_OK;
        }

        public abstract int GetDefaultCaps(out VideoStreamConfigCaps _caps);

        public int GetStreamCaps(int iIndex, out AMMediaType ppmt, out VideoStreamConfigCaps _caps)
        {
            ppmt = null;
            _caps = null;
            if (iIndex < 0)
                return E_INVALIDARG;

            ppmt = new AMMediaType();
            int hr = GetMediaType(iIndex, ref ppmt);

            if (FAILED(hr))
                return hr;

            if (hr == VFW_S_NO_MORE_ITEMS)
                return S_FALSE;

            hr = GetDefaultCaps(out _caps);
            return hr;
        }

        public int GetStreamCaps(int iIndex, IntPtr ppmt, IntPtr pSCC)
        {
            AMMediaType pmt;
            VideoStreamConfigCaps _caps;
            HRESULT hr = (HRESULT)GetStreamCaps(iIndex, out pmt, out _caps);
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
                        hr = (HRESULT)SetMediaType(_newType);
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
                hr = (HRESULT)SetMediaType(_newType);
            }
            return hr;
        }

        public int GetFormat(out AMMediaType pmt)
        {
            pmt = new AMMediaType(m_mt);
            return NOERROR;
        }
    }
}
