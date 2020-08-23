//using DirectShow;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Runtime.InteropServices;
//using System.Text;

//namespace Clowd.Com.Video
//{
//    class NvfbcFrameProvider : IFrameProvider
//    {
//        public NvfbcFrameProvider()
//        {

//        }
//        public int CopyScreenToSamplePtr(ref IMediaSampleImpl _sample)
//        {
//            throw new NotImplementedException();
//        }

//        public void Dispose()
//        {
//            throw new NotImplementedException();
//        }

//        public int GetCaptureProperties(out CaptureProperties properties)
//        {
//            throw new NotImplementedException();
//        }

//        public int SetCaptureProperties(CaptureProperties properties)
//        {
//            throw new NotImplementedException();
//        }
//    }

//    class NvfbcWrapper
//    {
//#if _WIN64
//        const string NVFBC_DLL = "NvFBC64.dll";
//#else
//        const string NVFBC_DLL = "NvFBC.dll";
//#endif


//        //[DllImport(NVFBC_DLL, EntryPoint = "NvFBC_GetSDKVersion")]
//        //GetSDKVersion


//    }
//    //NvIFR64.dll:

//    //NvIFR_ConnectToCrossProcessSharedSurfaceEXT

//    //NvIFR_CopyFromCrossProcessSharedSurfaceEXT

//    //NvIFR_CopyFromSharedSurfaceEXT

//    //NvIFR_CopyToCrossProcessSharedSurfaceEXT

//    //NvIFR_CopyToSharedSurfaceEXT

//    //NvIFR_Create

//    //NvIFR_CreateCrossProcessSharedSurfaceEXT

//    //NvIFR_CreateEx

//    //NvIFR_CreateSharedSurfaceEXT

//    //NvIFR_DestroyCrossProcessSharedSurfaceEXT

//    //NvIFR_DestroySharedSurfaceEXT

//    //NvIFR_GetSDKVersion

//    //NvIFROpenGL64.dll:

//    //NvIFROGLCreateInstance

//    //NvFBC64.dll:

//    //NvFBC_Create

//    //NvFBC_CreateEx

//    //NvFBC_Enable

//    //NvFBC_GetSDKVersion

//    //NvFBC_GetStatus

//    //NvFBC_GetStatusEx

//    //NvFBC_SetGlobalFlags

//}
