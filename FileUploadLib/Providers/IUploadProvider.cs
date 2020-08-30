using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileUploadLib
{
    public delegate void ProgressHandler(long bytesUploaded);
    public interface IUploadProvider
    {
        Task<UploadResult> Upload(Stream fileStream, string fileName, ProgressHandler progress);
        Task<Stream> BeginLargeUpload(string fileName, bool isContentGzipped);
        Task<UploadResult> EndLargeUpload(Stream genStream);
    }
}
