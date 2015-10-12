using Clowd.Shared;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickShareServer
{
    public interface IMimeTypeHandler
    {
        bool CheckDoesHandleMime(string mimeType);
        void FinalizeUploadMetadata(string mimeType, string displayName, byte[] data, CloudBlockBlob blob);
        void PopulateRazorTemplate(string mimeType, string displayName, RazorUploadTemplate template, CloudBlockBlob blob);
    }
}
