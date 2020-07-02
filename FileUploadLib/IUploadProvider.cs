using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileUploadLib
{
    public delegate void ProgressHandler(long bytesUploaded);
    public interface IUploadProvider<in TOptions> 
        where TOptions : UploadOptions 
    {
        Task<UploadResult> Upload(Stream fileStream, TOptions options, ProgressHandler progress);
    }
}
